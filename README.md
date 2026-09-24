# Rappy Runs Client (C#)

The desktop client being rewritten from LispWorks (`client/`) to C# + WebView2
(issue #322). Plan and porting specs: [docs/PLAN.md](docs/PLAN.md).

## Layout

```
RappyRuns.sln
src/RappyRuns.Core/   pure logic (no Win32), i18n table
src/RappyRuns.App/    RappyRunsClient.exe: WinForms + WebView2 host, IPC
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
