# Rappy Runs Client (C#)

The desktop client being rewritten from LispWorks (`client/`) to C# + WebView2
(issue #322). Plan and porting specs: [docs/PLAN.md](docs/PLAN.md).

## Layout

```
RappyRuns.sln
src/RappyRuns.Core/   pure logic (no Win32), i18n table
src/RappyRuns.Win/    Win32/WinRT: memory, capture, audio, overlay, tray, Pin Share I/O
src/RappyRuns.Host/   composition root: ClientHost (services, startup/quit), PollLoop,
                      UiState (the `state` patches), Ipc/*Methods (docs/ipc.md)
src/RappyRuns.App/    RappyRunsClient.exe: Program (startup), WinForms + WebView2 window, IPC transport
ui/                   Svelte + TypeScript UI, embedded into the exe
tests/RappyRuns.Tests xUnit; golden/ holds outputs exported from the Lisp client
tools/                SBCL scripts that export Lisp data (i18n, goldens)
package.ps1           build + dist/RappyRunsClient.zip (same layout as the Lisp zip)
```

## Build

Needs the .NET 10 SDK and Node 24.

```powershell
dotnet test RappyRuns.sln          # also builds the UI (npm) on first run
./package.ps1                      # dev zip; -Version X.Y.Z for a release build
```

`dotnet run --project src/RappyRuns.App -- --debug` starts the client with
DevTools enabled. For UI hot reload run `npm run dev` in `ui/` and start the
exe with `RAPPYRUNS_UI_DEV_URL=http://localhost:5173/`.

### Running next to the installed client

The single-instance guard is shared with the Lisp client, so a dev build
normally just raises the installed one and exits. For side-by-side checks:

```powershell
$env:RAPPYRUNS_CONFIG_DIR = "$env:TEMP\rr-dev"   # config, queue, trigger log, recording log, WebView2 profile
$env:RAPPYRUNS_DEV_MULTI = "1"                   # or pass --no-single-instance
dotnet run --project src/RappyRuns.App
```

With the config folder redirected the installed client's files are never
touched (no startup marker, no %TEMP% update sweep, its own autostart value).
The multi-instance switch uses its own tray window class and forces recording
and the Pin Share relay off: their pipe names and the addon's exchange files
are fixed, so two copies would collide. `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333`
exposes the page to CDP for scripted checks.

## i18n

`src/RappyRuns.Core/I18n/strings.json` is generated from `client/src/i18n.lisp`
together with a golden file of Lisp `FORMAT` outputs. While the Lisp client is
still the source, change strings there and re-run from the repo root:

```
sbcl --script desktop/tools/export-i18n.lisp
```

Template syntax: `{n}` argument, `{n?text}` text only when argument n is
non-null, `{n#one|many}` plural. C# (`Template.cs`) and TypeScript (`i18n.ts`)
both test against the golden file.
