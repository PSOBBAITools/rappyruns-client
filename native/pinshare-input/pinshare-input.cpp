// pinshare-input.cpp - lets Pin Share's bindings take priority over the game.
//
// The Pin Share addon loads this DLL with LuaJIT's ffi.load and tells it which
// keyboard keys and controller buttons it has bound. The DLL then hides exactly
// those inputs from the game, so pressing a bound key no longer also triggers
// the game's own function. Unbound keys are never touched.
//
// Keyboard: the game reads the keyboard with IDirectInputDevice8::GetDeviceState
// through the addon plugin's wrapper (ImguiDInputDevice), and the plugin raises
// the addons' key_pressed events inside that wrapper. We patch the wrapper's
// vtable, call through (the plugin and the addon see the key), and clear the
// bound keys from the result only on its way back to the game.
//
// Controller: ephinea.dll reads the pad with XInputGetState (xinput1_4.dll).
// We hook it at Windows' hot-patch point, raise the bound buttons' presses as
// events the addon polls, and clear those buttons from the state the game gets.
// A combo binding (modifier + button) hides only the button, and only when it
// is pressed while the modifier is held; the modifier still reaches the game.
//
// Safety: every mask lapses when the addon stops polling for 2 seconds (addon
// disabled, errored or reloaded), so inputs can never stay hidden by accident.
// The module pins itself: patched code points into it, so it must never unload.
//
// Build: build.cmd (VS2022 x86). PsoBB.exe is a 32-bit process.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <intrin.h>
#include <initguid.h>
#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>
#include <xinput.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "psapi.lib")

#define PINSHARE_INPUT_VERSION 1
#define EXPORT extern "C" __declspec(dllexport)

// ---------------------------------------------------------------- log
static char g_logPath[MAX_PATH];
static CRITICAL_SECTION g_logCs;
static int g_logLines;

static void LogF(const char* fmt, ...)
{
    EnterCriticalSection(&g_logCs);
    if (g_logLines++ < 200) {                   // init and errors only; never grows
        FILE* f = NULL;
        if (fopen_s(&f, g_logPath, g_logLines == 1 ? "w" : "a") == 0 && f) {
            SYSTEMTIME t; GetLocalTime(&t);
            fprintf(f, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
            va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
            fputc('\n', f);
            fclose(f);
        }
    }
    LeaveCriticalSection(&g_logCs);
}

// ---------------------------------------------------------------- shared state
enum { HEARTBEAT_MS = 2000, MAX_PAD_BINDINGS = 16 };
enum { PAD_LT = 0x10000, PAD_RT = 0x20000 };   // triggers as pseudo buttons
enum { TRIGGER_DOWN = 64, TRIGGER_UP = 32 };   // hysteresis on the 0-255 trigger value

static volatile LONG g_heartbeat;               // GetTickCount of the last addon call
static volatile LONG g_keyMask[256];            // DIK -> 1 = hide from the game
static CRITICAL_SECTION g_padCs;
static DWORD g_padMain[MAX_PAD_BINDINGS], g_padMod[MAX_PAD_BINDINGS];
static int g_padCount;
static volatile LONG g_fired;                   // binding index bits pressed since the last poll
static volatile LONG g_padRaw[4];               // latest unmasked buttons per XInput user (0 = none/disconnected)
static volatile LONG g_status;                  // bit 0 keyboard hooked, bit 1 controller hooked, bit 2 init done

static bool Alive() { return (LONG)(GetTickCount() - (DWORD)g_heartbeat) < HEARTBEAT_MS; }
static void Beat() { InterlockedExchange(&g_heartbeat, (LONG)GetTickCount()); }

// ---------------------------------------------------------------- keyboard (plugin's DirectInput wrapper)
typedef HRESULT (STDMETHODCALLTYPE *GetState_t)(void*, DWORD, LPVOID);
enum { SLOT_GETSTATE = 9, DEVICE8_SLOTS = 30 };  // IUnknown(3) + IDirectInputDevice8 methods
static GetState_t g_origGetState;

static HRESULT STDMETHODCALLTYPE HookGetState(void* self, DWORD cb, LPVOID data)
{
    HRESULT hr = g_origGetState(self, cb, data);
    // 256 bytes is only ever the keyboard (c_dfDIKeyboard); mouse and joystick formats differ.
    if (SUCCEEDED(hr) && cb == 256 && data && Alive()) {
        BYTE* keys = (BYTE*)data;
        for (int dik = 0; dik < 256; dik++) if (g_keyMask[dik]) keys[dik] = 0;
    }
    return hr;
}

struct Range { BYTE* base; SIZE_T size; };

template <typename F> static void ForEachReadableRegion(const Range& r, F f)
{
    BYTE* p = r.base; BYTE* end = r.base + r.size;
    while (p < end) {
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery(p, &mbi, sizeof mbi)) break;
        BYTE* rb = (BYTE*)mbi.BaseAddress; SIZE_T rs = mbi.RegionSize;
        if (rb + rs > end) rs = end - rb;
        if (mbi.State == MEM_COMMIT && !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) && (mbi.Protect & 0xEE)) f(rb, rs);
        p = rb + mbi.RegionSize;
    }
}
static BYTE* FindBytes(const Range& r, const void* pat, SIZE_T n)
{
    BYTE* found = NULL;
    ForEachReadableRegion(r, [&](BYTE* b, SIZE_T s) {
        if (found || s < n) return;
        for (SIZE_T i = 0; i + n <= s; i++) if (b[i] == ((const BYTE*)pat)[0] && memcmp(b + i, pat, n) == 0) { found = b + i; return; }
    });
    return found;
}
template <typename F> static void ForEachDwordRef(const Range& r, DWORD value, F f)
{
    ForEachReadableRegion(r, [&](BYTE* b, SIZE_T s) {
        for (SIZE_T i = 0; i + 4 <= s; i += 4) if (*(DWORD*)(b + i) == value) f(b + i);
    });
}
static bool InRange(const Range& r, const void* p) { return (BYTE*)p >= r.base && (BYTE*)p < r.base + r.size; }

// MSVC RTTI: type name -> TypeDescriptor -> CompleteObjectLocator -> the vtable after it.
// (Same walk as psobb-camera's ceiling mod uses for ImguiD3D8Device.)
static void** FindVtableByRtti(const Range& mod, const char* typeName)
{
    BYTE* name = FindBytes(mod, typeName, strlen(typeName) + 1);
    if (!name) return NULL;
    BYTE* td = name - 8;
    void** result = NULL;
    ForEachDwordRef(mod, (DWORD)td, [&](BYTE* ref) {
        if (result) return;
        BYTE* col = ref - 12;
        if (!InRange(mod, col)) return;
        if (*(DWORD*)col != 0 || *(DWORD*)(col + 4) != 0) return;   // primary vtable only
        ForEachDwordRef(mod, (DWORD)col, [&](BYTE* vref) {
            if (result) return;
            void** vt = (void**)(vref + 4);
            int ok = 0;
            // GetDeviceState may already point elsewhere (another tool chained on it).
            for (int i = 0; i < DEVICE8_SLOTS; i++) { if (InRange(mod, vt[i]) || i == SLOT_GETSTATE) ok++; else break; }
            if (ok == DEVICE8_SLOTS) result = vt;
        });
    });
    return result;
}

static bool HookKeyboard()
{
    HMODULE mods[512]; DWORD cb = 0;
    if (!EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &cb)) return false;
    if (cb > sizeof mods) cb = sizeof mods;      // cb is what all modules would need
    for (DWORD i = 0; i < cb / sizeof(HMODULE); i++) {
        char path[MAX_PATH];
        if (!GetModuleFileNameA(mods[i], path, MAX_PATH)) continue;
        const char* base = strrchr(path, '\\'); base = base ? base + 1 : path;
        if (_stricmp(base, "dinput8.dll") != 0) continue;           // the plugin (and maybe customdlls)
        MODULEINFO mi; if (!GetModuleInformation(GetCurrentProcess(), mods[i], &mi, sizeof mi)) continue;
        Range r = { (BYTE*)mi.lpBaseOfDll, mi.SizeOfImage };
        void** vt = FindVtableByRtti(r, ".?AVImguiDInputDevice@@");
        if (!vt) continue;
        DWORD old;
        if (!VirtualProtect(vt + SLOT_GETSTATE, sizeof(void*), PAGE_READWRITE, &old)) {
            LogF("keyboard: VirtualProtect failed err=%u", GetLastError());
            return false;
        }
        g_origGetState = (GetState_t)vt[SLOT_GETSTATE];
        InterlockedExchangePointer(&vt[SLOT_GETSTATE], (void*)HookGetState);
        VirtualProtect(vt + SLOT_GETSTATE, sizeof(void*), old, &old);
        LogF("keyboard: hooked ImguiDInputDevice in %s", path);
        return true;
    }
    return false;
}

// ---------------------------------------------------------------- controller (XInput)
typedef DWORD (WINAPI *XGetState_t)(DWORD, XINPUT_STATE*);
static DWORD g_prev[4], g_hidden[4];
static DWORD g_pendingSingle[4];                // held single-bound modifiers waiting for release
static DWORD g_usedAsMod[4];                    // ...of which a combo was fired while held

// Runs on every XInputGetState the game makes (~120 Hz from ephinea.dll).
static void FilterPad(DWORD user, XINPUT_STATE* st)
{
    if (user >= 4 || !st) return;
    XINPUT_GAMEPAD* g = &st->Gamepad;
    DWORD raw = g->wButtons;
    if (g->bLeftTrigger >= TRIGGER_DOWN || ((g_prev[user] & PAD_LT) && g->bLeftTrigger > TRIGGER_UP)) raw |= PAD_LT;
    if (g->bRightTrigger >= TRIGGER_DOWN || ((g_prev[user] & PAD_RT) && g->bRightTrigger > TRIGGER_UP)) raw |= PAD_RT;
    DWORD pressed = raw & ~g_prev[user];
    g_prev[user] = raw;
    InterlockedExchange(&g_padRaw[user], (LONG)raw);

    if (!Alive()) { g_hidden[user] = g_pendingSingle[user] = g_usedAsMod[user] = 0; return; }
    // A button is hidden from the press that matched a binding until it is released,
    // so the game never sees half a press when the modifier is let go first.
    g_hidden[user] &= raw;
    DWORD released = g_pendingSingle[user] & ~raw;
    DWORD fired = 0;
    EnterCriticalSection(&g_padCs);
    DWORD mods = 0;                                        // buttons some combo uses as modifier
    for (int i = 0; i < g_padCount; i++) mods |= g_padMod[i];
    if (pressed) {
        DWORD taken = 0;
        for (int pass = 0; pass < 2; pass++) {             // combos first, then single buttons
            for (int i = 0; i < g_padCount; i++) {
                DWORD main = g_padMain[i], mod = g_padMod[i];
                if (!main || (pass == 0) != (mod != 0)) continue;
                if (!(pressed & main) || (taken & main)) continue;
                if (mod && (raw & mod) != mod) continue;
                taken |= main;
                if (mod) { g_usedAsMod[user] |= mod; fired |= 1u << i; }
                // Bound alone but also a combo's modifier: decide on release, so pressing
                // the combo does not also run the single action.
                else if (main & mods) g_pendingSingle[user] |= main;
                else fired |= 1u << i;
            }
        }
        g_hidden[user] |= taken;
    }
    if (released) {
        DWORD taken = 0;                                   // like the press path: first binding wins
        for (int i = 0; i < g_padCount; i++) {
            DWORD main = g_padMain[i];
            if (g_padMod[i] || !(released & main) || (taken & main)) continue;
            taken |= main;
            if (!(g_usedAsMod[user] & main)) fired |= 1u << i;
        }
        g_pendingSingle[user] &= ~released;
    }
    g_usedAsMod[user] &= raw;                              // a modifier's mark ends with its press
    LeaveCriticalSection(&g_padCs);
    if (fired) _InterlockedOr(&g_fired, (LONG)fired);
    DWORD hide = g_hidden[user];
    if (hide) {
        g->wButtons &= (WORD)~(hide & 0xFFFF);
        if (hide & PAD_LT) g->bLeftTrigger = 0;
        if (hide & PAD_RT) g->bRightTrigger = 0;
    }
}

static XGetState_t g_origX[4];
#define XHOOK(n) static DWORD WINAPI XHook##n(DWORD user, XINPUT_STATE* st) { \
        DWORD rc = g_origX[n](user, st);         if (rc == ERROR_SUCCESS) FilterPad(user, st); else if (user < 4) InterlockedExchange(&g_padRaw[user], 0);         return rc; }
XHOOK(0) XHOOK(1) XHOOK(2) XHOOK(3)
static void* g_xhooks[4] = { XHook0, XHook1, XHook2, XHook3 };
static int g_nx;

// Windows' hot-patch point: 5 filler bytes before the function and a 2-byte
// `mov edi, edi` at its entry. If another tool already took it, chain to that tool.
static bool HotPatch(BYTE* fn, const char* label)
{
    if (!fn || g_nx >= 4) return false;
    BYTE* pad = fn - 5;
    XGetState_t orig;
    if (fn[0] == 0x8B && fn[1] == 0xFF) {
        for (int i = 0; i < 5; i++) if (pad[i] != 0xCC && pad[i] != 0x90) { LogF("%s: hot-patch area in use", label); return false; }
        orig = (XGetState_t)(fn + 2);
    } else if (fn[0] == 0xEB && fn[1] == 0xF9 && pad[0] == 0xE9) {
        orig = (XGetState_t)(pad + 5 + *(LONG*)(pad + 1));
        LogF("%s: already hooked, chaining to %p", label, orig);
    } else {
        LogF("%s: no hot-patch prologue (%02x %02x)", label, fn[0], fn[1]);
        return false;
    }
    g_origX[g_nx] = orig;
    DWORD old;
    if (!VirtualProtect(pad, 7, PAGE_EXECUTE_READWRITE, &old)) return false;
    pad[0] = 0xE9;
    *(LONG*)(pad + 1) = (LONG)((BYTE*)g_xhooks[g_nx] - (pad + 5));
    *(volatile WORD*)fn = 0xF9EB;                          // jmp short -7
    VirtualProtect(pad, 7, old, &old);
    FlushInstructionCache(GetCurrentProcess(), pad, 7);
    g_nx++;
    LogF("controller: hooked %s", label);
    return true;
}

static bool HookController()
{
    // ephinea.dll uses xinput1_4; load it ourselves if the game has not yet (same module then).
    HMODULE m = GetModuleHandleA("xinput1_4.dll");
    if (!m) m = LoadLibraryA("xinput1_4.dll");
    bool ok = false;
    if (m) {
        BYTE* plain = (BYTE*)GetProcAddress(m, "XInputGetState");
        BYTE* ex = (BYTE*)GetProcAddress(m, (LPCSTR)100);   // XInputGetStateEx (adds the guide button)
        ok = HotPatch(plain, "xinput1_4!XInputGetState");
        if (ex && ex != plain) HotPatch(ex, "xinput1_4!#100");
    }
    const char* legacy[] = { "xinput1_3.dll", "xinput9_1_0.dll" };
    for (int i = 0; i < 2; i++) {
        HMODULE l = GetModuleHandleA(legacy[i]);
        if (l && HotPatch((BYTE*)GetProcAddress(l, "XInputGetState"), legacy[i])) ok = true;
    }
    return ok;
}

static DWORD WINAPI InitThread(LPVOID)
{
    if (HookController()) _InterlockedOr(&g_status, 2);
    // The plugin is loaded before any addon runs, but be patient on a slow start.
    bool hooked = false;
    for (int i = 0; i < 50 && !hooked; i++) {
        hooked = HookKeyboard();
        if (!hooked) Sleep(200);
    }
    if (hooked) _InterlockedOr(&g_status, 1);
    else LogF("keyboard: ImguiDInputDevice not found (addon plugin too old or too new?)");
    _InterlockedOr(&g_status, 4);
    return 0;
}

// DirectInput key codes are scan codes with the 0xE0 extended prefix folded into bit 7
// (Delete = 0xD3, not numpad '.' 0x53). MapVirtualKey cannot tell the navigation cluster
// from the numpad (it answers 0x53 for VK_DELETE even with VSC_EX), so those are listed.
static int VkToDik(UINT vk)
{
    static const struct { UINT vk; int dik; } fixed[] = {
        { VK_INSERT, DIK_INSERT }, { VK_DELETE, DIK_DELETE }, { VK_HOME, DIK_HOME }, { VK_END, DIK_END },
        { VK_PRIOR, DIK_PRIOR }, { VK_NEXT, DIK_NEXT }, { VK_LEFT, DIK_LEFT }, { VK_UP, DIK_UP },
        { VK_RIGHT, DIK_RIGHT }, { VK_DOWN, DIK_DOWN }, { VK_DIVIDE, DIK_DIVIDE }, { VK_SNAPSHOT, DIK_SYSRQ },
        { VK_PAUSE, DIK_PAUSE }, { VK_NUMLOCK, DIK_NUMLOCK }, { VK_RCONTROL, DIK_RCONTROL },
        { VK_RMENU, DIK_RMENU }, { VK_LWIN, DIK_LWIN }, { VK_RWIN, DIK_RWIN }, { VK_APPS, DIK_APPS },
    };
    for (int i = 0; i < (int)(sizeof fixed / sizeof fixed[0]); i++) if (fixed[i].vk == vk) return fixed[i].dik;
    UINT sc = MapVirtualKeyA(vk, MAPVK_VK_TO_VSC_EX);
    if ((sc & 0xFF00) == 0xE000) return 0x80 | (sc & 0x7F);
    if (sc & 0xFF00) return 0;                   // 0xE1 sequences: no DirectInput equivalent
    return (int)sc;
}

// A generic VK (Ctrl, Alt, Shift, Enter) stands for two physical keys; hide both.
static int VkToDiks(UINT vk, int out[2])
{
    switch (vk) {
    case VK_CONTROL: out[0] = DIK_LCONTROL; out[1] = DIK_RCONTROL; return 2;
    case VK_MENU:    out[0] = DIK_LMENU;    out[1] = DIK_RMENU;    return 2;
    case VK_SHIFT:   out[0] = DIK_LSHIFT;   out[1] = DIK_RSHIFT;   return 2;
    case VK_RETURN:  out[0] = DIK_RETURN;   out[1] = DIK_NUMPADENTER; return 2;
    }
    out[0] = VkToDik(vk);
    return 1;
}

// ---------------------------------------------------------------- exports (cdecl, for LuaJIT ffi)
EXPORT int __cdecl pinshare_input_version(void) { return PINSHARE_INPUT_VERSION; }

// bit 0 = keyboard hooked, bit 1 = controller hooked, bit 2 = done trying (bits 0-1 are final)
EXPORT int __cdecl pinshare_input_status(void) { return (int)g_status; }

// The virtual-key codes to hide from the game (replaces the previous set).
EXPORT void __cdecl pinshare_input_set_keys(const int* vks, int n)
{
    LONG next[256] = { 0 };
    for (int i = 0; vks && i < n; i++) {
        int diks[2];
        for (int k = VkToDiks((UINT)vks[i], diks) - 1; k >= 0; k--)
            if (diks[k] > 0 && diks[k] < 256) next[diks[k]] = 1;
    }
    for (int dik = 0; dik < 256; dik++) InterlockedExchange(&g_keyMask[dik], next[dik]);
    Beat();
}

// Controller bindings: button i is mains[i] (one XINPUT_GAMEPAD bit, or PAD_LT / PAD_RT),
// pressed while holding mods[i] (0 = alone). Index i is the bit reported by poll.
EXPORT void __cdecl pinshare_input_set_pad(const unsigned* mains, const unsigned* mods, int n)
{
    if (n > MAX_PAD_BINDINGS) n = MAX_PAD_BINDINGS;
    EnterCriticalSection(&g_padCs);
    g_padCount = 0;
    for (int i = 0; mains && i < n; i++) {
        g_padMain[i] = mains[i];
        g_padMod[i] = (mods && !(mods[i] & mains[i])) ? mods[i] : 0;
    }
    g_padCount = (mains && n > 0) ? n : 0;
    LeaveCriticalSection(&g_padCs);
    Beat();
}

// Call every frame: keeps the masks alive and returns the bindings pressed since the last call.
EXPORT unsigned __cdecl pinshare_input_poll(void)
{
    bool wasAlive = Alive();
    Beat();
    LONG fired = InterlockedExchange(&g_fired, 0);
    // Presses from before a lapse (addon off, errored, reloading) are stale: drop them.
    return wasAlive ? (unsigned)fired : 0;
}

// The controller buttons held right now, before any hiding (for the binding UI).
EXPORT unsigned __cdecl pinshare_input_pad_raw(void)
{
    return (unsigned)(g_padRaw[0] | g_padRaw[1] | g_padRaw[2] | g_padRaw[3]);
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        InitializeCriticalSection(&g_logCs);
        InitializeCriticalSection(&g_padCs);
        GetModuleFileNameA(hinst, g_logPath, MAX_PATH);
        char* dot = strrchr(g_logPath, '.'); if (dot) strcpy_s(dot, 5, ".log");
        HMODULE self;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCSTR)DllMain, &self);
        LogF("pinshare-input %d loaded", PINSHARE_INPUT_VERSION);
        CloseHandle(CreateThread(NULL, 0, InitThread, NULL, 0, NULL));
    }
    return TRUE;
}
