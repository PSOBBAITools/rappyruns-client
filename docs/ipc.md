# Host <-> UI IPC contract

The WebView2 UI (`desktop/ui`) and the host (`RappyRuns.App`) talk in JSON messages
(PLAN.md "P0 で決めた契約"):

```
UI -> host   {"kind":"request","id":7,"method":"area.verb","params":{...}}
host -> UI   {"kind":"response","id":7,"ok":true,"result":...}
             {"kind":"response","id":7,"ok":false,"error":"message"}
host -> UI   {"kind":"event","name":"state","data":...}
```

- Field names are camelCase (`JsonSerializerDefaults.Web`). `null` is sent explicitly.
- TypeScript is the reference for the shapes: `desktop/ui/src/lib/ipc-types.ts` (data) and
  `desktop/ui/src/lib/ipc.ts` (typed wrappers, one object per area). Keep this file, those two,
  and `Host/IpcHost.cs` + the C# DTOs in step.
- `ok:false` is for transport/programming failures only (unknown method, bad params, an exception).
  Expected outcomes (validation errors, "server rejected", "no recording") come back as `ok:true`
  with a **Notice** so the UI can show them in the current language.
- A dev mock of the whole contract is `desktop/ui/src/lib/mock-host.ts` (`npm run dev`, open
  `http://localhost:5173/?mock`, `?mock=guest`, `?mock=notice`). When in doubt about behavior,
  it is a runnable example.

## Common shapes

### Msg: localizable text

The host never sends translated text. It sends the i18n key and its arguments; the UI formats
with the strings table it got from `app.hello` / `app.setLanguage`. A language switch is then
instant and needs no re-render on the host.

```ts
type MsgArg = string | number | null | Msg;          // a nested Msg is rendered first
type Msg = { key: string; args?: MsgArg[] } | { text: string };   // text = not localized
```

Examples:

| Lisp | Msg |
|---|---|
| `(tr :token-ok "teapot")` | `{key:"token-ok",args:["teapot"]}` |
| `(tr :version-status "0.61.0" nil)` | `{key:"version-status",args:["0.61.0",null]}` |
| `(tr :version-status v (tr :update-up-to-date))` | `{key:"version-status",args:["0.61.0",{key:"update-up-to-date"}]}` |
| `server-status-error-text` with a WinHTTP hint | `{key:"server-error-prefix",args:[{key:"hint-connect"}]}` |
| `(tr :status-failed reason)` | `{key:"status-failed",args:["<api error text>"]}` or with a nested hint Msg |

Keys come from `strings.json` (generated from i18n.lisp) plus `strings.extra.json` (C#-only keys,
see below). Argument order and null-ness follow the `{n}` / `{n?…}` / `{n#…}` templates.

### Line: a status line

```ts
type Tone = 'neutral' | 'ok' | 'busy' | 'error';     // 'error' = the Lisp client's red text
interface Line { msg: Msg; tone?: Tone }             // tone omitted = neutral
```

Suggested tones (the UI draws a colored dot): game attached `ok`, searching `busy`,
signature refused / read failed / recording error `error`; server OK `ok`, failure `error`;
token OK `ok`, checking / pairing waiting / login.txt checking `busy`, unlinked **neutral**
(ui-shell R20: not an error), invalid / failures `error`; Pin Share per the table in
ui-shell §1.4.5 (errors `error`, connected `ok`, connecting `busy`, others neutral).

### Notice: a message box

```ts
interface Notice { message: Msg; error?: boolean; confirmUrl?: string }
```

Shown as an OK box (`error` = red badge). With `confirmUrl` it is a Yes/No question and Yes
calls `app.openExternal(confirmUrl)` (used for `:update-not-writable-confirm`).
Message boxes are in-page modals: the host keeps sending `state` and the tray keeps working.

### Trigger (same JSON as `POST /api/quests`)

```ts
{type:"monster",monster:N} | {type:"floor-switch",floor:F,switch:S} | {type:"register",register:N} | {type:"warp-in"}
```

## Events (host -> UI)

### `state` — `Partial<HostState>`

The 4 Hz status. The host keeps the last `HostState` it sent and, on each GUI tick (and
immediately after any change it knows about), sends **only the top-level keys whose JSON
changed**; each key replaces the old value whole. Nothing changed = no event (ui-shell R1).
Reference implementation: `diffState` / `applyStatePatch` in `ui/src/lib/state.ts` (tested).

```ts
interface HostState {
  moderator: boolean;        // Rooms tab, rule registration (/api/me role, cached in config :moderator)
  pinshareAllowed: boolean;  // limited rollout (/api/me features); Pin Share group shown only when true
  game: Line;                // ui-shell §1.2.1 game-status (incl. :game-status-with-error)
  server: Line;              // server-status
  token: Line;               // token-status (also pairing / login.txt progress)
  quest: QuestStatus;        // quest-status, structured (below)
  pinshare: Line;            // pinshare-status-text
  pinSet: Line;              // pinshare-pin-set-text
  version: Line;             // updates group: :version-status <version> <note Msg|null>
  pairing: boolean;          // a pairing worker runs (Link button disabled)
  updating: boolean;         // an update check/download runs (Check button disabled)
  settings: Settings;        // below; sent whole when any setting changes
}

type QuestStatus =
  | { kind: 'none' }                                   // :no-active-quest
  | { kind: 'waiting'; name: string }                  // :quest-waiting <quest name>
  | { kind: 'active'; slug: string; others: number;    // " (+N)"
      elapsed: string;                                 // m:ss, formatted ONCE, same string as the window title (R21)
      recording: boolean;                              // [REC]
      ghost: { target: string; gap: string | null } | null };  // " | vs <target> <gap>"
```

The UI rebuilds the exact Lisp line with `questStatusText` (`format.ts`) for tooltips; the
window title stays host-side (`"<elapsed><gap>[ [REC]] - Rappy Runs Client"`).

`Settings` (config.sexp keys in camelCase unless noted):

| field | config | notes |
|---|---|---|
| `language` | `:language` | `"en"`/`"ja"` |
| `serverUrl` | `:server-url` | field shown only when `AppSnapshot.debug` |
| `apiToken` | `:api-token` | the password field; pairing / login.txt updates it here (R19) |
| `trackingOnly`, `trackingPrivate`, `recordAudio` | same | |
| `recordMaxTotalGb` | `:record-max-total-gb` | note hidden when 0 |
| `autoPublish` | `:auto-publish` | cache of the server value |
| `recordDir` | `:record-dir` | **resolved** folder (`resolve-record-dir`), never "" |
| `ghostRace`, `ghostOverlay`, `ghostMarker` | same | |
| `overlayCorner` | `:overlay-corner` | `top-right…bottom-left`, `custom`; Ctrl+drag of the overlay sets `custom` and must emit `state` |
| `pinshareEnabled`, `pinshareChannel` | same | |
| `autoUpdate`, `closeToTray`, `startMinimized`, `rankToast`, `triggerLog` | same | |
| `autostart` | registry | `autostart-enabled-p` (Run value points at this exe) |

### `runs` — `RunRow[]`

The whole Runs list, sent **only when it changes** (run completed, video linked, upload % moved
by a whole percent, upload done/given up, retry results, clear, language switch not needed).
The UI keys rows by `id`, so selection survives (R2). Newest first, as `*runs*`.

```ts
interface RunRow {
  id: string;            // stable GUID of the queue entry (core §9.3: never EQ-by-copy)
  quest: string;         // :quest-name, else :quest-slug
  timeMs: number;        // UI formats with formatRunTime (m:ss.mmm, minutes past an hour)
  players: number; pb: boolean;       // "4P", "1P/PB"
  video: Msg | null;     // run-video-label; video-uploading carries the % as args[0] (UI draws a bar)
  status: Msg[];         // run-status-label, then entry notes (run-standing-note, ghost-note); UI joins " · "
  statusError?: boolean; // red (rejected / failed)
  url: string | null;    // run page; null while unsent
  hasRecording: boolean; // :video-path present
}
```

### `rooms` — `RoomRow[]`

The Rooms tab rows (`run-room-rows`), sent when `rooms-list-signature` changes (moderators only;
may be skipped entirely for non-moderators).

```ts
interface RoomRow { id: string; area: string; kind: 'clear' | 'enemy'; name: string | null; trigger: Trigger | null }
```

The UI renders the condition (`:rooms-clear` or the enemy name) and the trigger label
(`warp-in` / `register:N` / `floor-switch:F:S` / `monster:ID`, `triggerLabel` in `format.ts`).

### `notice` — `Notice`

Host-initiated message boxes: `report-startup-update` (`:update-download-failed-dialog`,
`:update-not-writable-confirm` with `confirmUrl`), and anything else a worker must report
when no request is waiting for it. Results of UI requests come back in the response instead.

## Methods (UI -> host)

"→ Notice?" means the result is `Notice | null` (null = nothing to show).

### app — `AppService` (P0) + config

| method | params | result | notes |
|---|---|---|---|
| `app.hello` | – | `AppSnapshot` | Writes the startup marker (P0). `AppSnapshot = {version, debug, language, languages:[{code,label}], strings, state: HostState, runs: RunRow[], rooms: RoomRow[]}`. `strings` = merged strings.json + strings.extra.json for the language. The UI opens on Settings when `state.settings.apiToken` is blank, else Runs (ui-shell §1). |
| `app.setLanguage` | `{language}` | `AppSnapshot` | Saves `:language`; updates tray menu/tooltip texts. No window rebuild (§1.4.1). Also emits `state` (settings.language). |
| `app.openExternal` | `{url}` | `null` | http(s) only (`valid-http-url-p`). |

### settings — config store (+ side effects)

| method | params | result | host module / behavior |
|---|---|---|---|
| `settings.set` | `{key, value}` | `Settings` | Save-and-apply for `trackingOnly, trackingPrivate, recordAudio, ghostRace, ghostOverlay, ghostMarker, overlayCorner, autoUpdate, closeToTray, startMinimized, rankToast`. Side effects: `ghostOverlay:false` → overlay hide now (overlay); `ghostMarker` → next 4 Hz overlay data. The UI disables `trackingPrivate` while `trackingOnly` is off. |
| `settings.saveConnection` | `{apiToken, serverUrl: string\|null}` | Notice? | Config + auth. `serverUrl` is null unless debug; trim trailing `/` and spaces; `normalize-token`; save; `check-server`; `check-token :notify t` and **await it**: return `:token-ok-dialog <user>` or `:token-rejected-dialog` (error); null for a blank token or a network failure (the token line shows it). |
| `settings.setAutoPublish` | `{enabled}` | `{enabled, notice: Notice\|null}` | Auth/API. The UI already asked `:auto-publish-confirm` when turning on. `POST /api/me/auto-publish`; on success save the cache; on failure return the last known server value and `:auto-publish-failed <msg>` (R18). |
| `settings.setAutostart` | `{enabled}` | `{enabled}` | Autostart (registry). Write, then **read back** and return the real state (R17). |
| `settings.chooseRecordDir` | – | `{recordDir}` or null | Recording. Native folder picker (`:choose-record-dir` title); saves at once; null on cancel. Emits `state`. |
| `settings.setTriggerLog` | `{enabled}` | Notice? | Trigger log. On: `start-trigger-log` then `:trigger-log-on <path>`; off: close. |

### account — auth

| method | params | result | notes |
|---|---|---|---|
| `account.link` | – | `null` | Browser pairing (ui-shell §2.1 / core §8.2). One worker; no-op while `state.pairing`. Progress and failures go to `state.token`; on completion save the token (emits `settings.apiToken`) and run check-token. |

login.txt login (§2.2) has no UI method: it runs at startup / on 401 and reports on `state.token`.

### runs — run queue, recordings, browser

| method | params | result | notes |
|---|---|---|---|
| `runs.open` | `{id}` | `null` | Opens the row's `url` (double-click / Enter / the row's open icon). No-op without url. |
| `runs.uploadVideo` | `{id: string\|null}` | Notice? | `upload-video-callback`: the row, or (null) the newest run a video can still be attached to. Explorer `/select,"<path>"` (R23) + `https://www.youtube.com/upload`. Errors: `:no-recording-for-run`, `:no-recordings-yet`, `:recording-file-missing <path>`. |
| `runs.openRecordingsFolder` | – | `null` | Create the folder, ShellExecute open. |
| `runs.openMyRuns` | – | `null` | `<server>/my/runs`. |
| `runs.retry` | – | `null` | Clears every video upload's counted-failure streak and backoff (reviving an upload the streak gave up, S17), then sets the retry flag; the poll loop submits on its next pass (works unlinked). Results arrive as `runs`. |
| `runs.clear` | – | `null` | The UI confirmed `:clear-list-confirm`. `clear-runs!`, then emits `runs`. |

### rules — quest rule registration (moderators, ui-shell §3)

| method | params | result | notes |
|---|---|---|---|
| `rules.prepare` | – | `{ok:true, parents:[{slug,name}], detected: slug\|null, rows: RoomRow[]}` or `{ok:false, notice}` | `GET /api/quests` → `timeable-quests`; `detected-parent` against `*run-quest*`; `rows` = `run-room-rows` (same as `rooms`). Failures: `:rule-no-parents`, `:rule-fetch-failed <msg>`. |
| `rules.create` | `{parent, name, description, end: Trigger, start: Trigger\|null}` | Notice | `POST /api/quests` (start omitted = inherit). `:rule-created <slug>` (then `check-server`), `:rule-duplicate`, `:rule-forbidden`, `:rule-rejected`, `:rule-post-failed` (error). |

Form logic lives in the UI (`ui/src/lib/rule-form.ts`, tested): end choices (Rooms preset
first, run rows with a trigger, then manual monster / floor-switch / register), manual value
ranges (id 0-65535, floor 0-17, switch/register 0-255), and the qrd-ok validation order.
Errors keep the form open; Register closes it and shows the returned notice.

### pinshare — Pin Share relay and pin sets (limited rollout)

| method | params | result | notes |
|---|---|---|---|
| `pinshare.setEnabled` | `{enabled, channel}` | `null` | Saves both (`toggle-pinshare-callback`); consent to write the addon. Relay reacts within a tick; status via `state.pinshare`. |
| `pinshare.applyChannel` | `{channel}` | `null` | Save the passphrase (host cleans it: control chars, trim, 64 chars). |
| `pinshare.saveNew` | – | Notice | Precheck (`:pinshare-save-no-quest`, `:pinshare-save-no-items`) then `POST /api/pin-sets`: `:pinshare-save-created <url>` / `:pinshare-save-failed <msg>`. |
| `pinshare.overwriteCheck` | – | `{ok:true, name, pins, arrows}` or `{ok:false, notice}` | `:pinshare-save-not-mine`, precheck errors. The UI then asks `:pinshare-save-confirm-overwrite <name> <pins> <arrows>`. |
| `pinshare.overwrite` | – | Notice | `POST /api/pin-sets/<id>/items`, refetch: `:pinshare-save-updated <pins> <arrows>` / failed. |

### updates — updater

| method | params | result | notes |
|---|---|---|---|
| `updates.check` | – | Notice? | Dev build → `:update-dev-build <releases url>`. Else set `state.updating` and `state.version` (`:update-checking`), check, and answer: failed → `:update-check-failed-dialog` (error); latest → `:update-latest-dialog <v>`; not writable → `:update-not-writable-confirm` with `confirmUrl`; download failed → `:update-download-failed-dialog` (error). A newer version downloads without asking: busy → `state.version` `:update-after-run`, return null; else restart (the process exits). |

## Host-side only (no IPC)

- Window title (`"<elapsed>… - Rappy Runs Client"`, set only on change), window close →
  close-to-tray or quit, start minimized, single instance, tray menu / balloons (ui-shell §5).
- Remembering window size is not needed (one window, tabs switch in place).

## Host module map

Implemented in `src/RappyRuns.Host`: `ClientHost` (services, startup/quit, status tick),
`UiState` (the `state` diff), `PollLoop`, and one `Ipc/<Area>Methods.cs` per area below.
`src/RappyRuns.App/Host/IpcHost.cs` is only the WebView2 transport.

| Area | Host module |
|---|---|
| `app.*` | `AppService` (App) + config store (Core) + tray texts (Win) |
| `settings.set` | config store (Core); overlay (Win) for ghost keys |
| `settings.saveConnection`, `account.link`, `settings.setAutoPublish` | auth / API client (Core): check-server, check-token, pairing, login.txt, `/api/me/auto-publish` |
| `settings.setAutostart` | autostart (Win, registry) |
| `settings.chooseRecordDir`, `runs.uploadVideo`, `runs.openRecordingsFolder` | recording (media) + shell helpers (Win) |
| `settings.setTriggerLog`, `rooms` event, `rules.prepare` rows | trigger log / room picker (Core) |
| `runs.*`, `runs` event | run queue / store (Core) + poll loop retry flag |
| `rules.*` | quest rules API (Core) |
| `pinshare.*` | Pin Share relay + pin sets (Core pure, Win threads) |
| `updates.check`, `notice` at startup | updater (Core + App) |
| `state` event | the 4 Hz GUI tick of the poll loop (App), fed by game/detector, recorder, ghost, auth, Pin Share |

## i18n keys added for the new UI

Kept in `desktop/src/RappyRuns.Core/I18n/strings.extra.json` (the generated `strings.json`
is overwritten by `desktop/tools/export-i18n.lisp`); the host merges both at load time.

| key | en | ja |
|---|---|---|
| `dialog-ok` | OK | OK |
| `dialog-yes` | Yes | はい |
| `dialog-no` | No | いいえ |
| `runs-empty` | No runs yet - finished quests appear here. | まだ記録はありません。… |
| `rooms-empty` | No rooms yet - play a quest and its rooms and enemies appear here. | まだ部屋はありません。… |
| `run-open-page` | Open the run page | 記録ページを開く |
| `video-untrimmed` | saved - check the end | 保存済み - 終わりを確認 |
