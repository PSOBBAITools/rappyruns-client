# Builds the C# client and packages dist/RappyRunsClient.zip in the layout the
# Lisp self-updater installs (spec core §10.7):
#   RappyRunsClient.exe                 self-contained single file
#   data/quest-triggers.sexp
#   data/pin-share/init.lua
#   data/pin-share/pinshare-input.dll  (-InputDll, or built with VS2022 C++ tools)
#   ffmpeg/ffmpeg.exe + LICENSE.txt    (optional, from client/vendor/ffmpeg/)
#
#   ./package.ps1                      dev build (no version: never self-updates)
#   ./package.ps1 -Version 1.0.0       release build; must match desktop/VERSION
#   ./package.ps1 -SkipTests -InputDll path\to\pinshare-input.dll
param(
    [string]$Version = "",
    [string]$InputDll = "",
    [switch]$SkipTests,
    [switch]$SkipInputDll
)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$client = Join-Path $root "..\client"
$dist = Join-Path $root "dist"

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z (got '$Version')." }
    $fileVersion = (Get-Content (Join-Path $root "VERSION") -Raw).Trim()
    if ($fileVersion -ne $Version) { throw "desktop/VERSION is $fileVersion, not $Version." }
}

Push-Location (Join-Path $root "ui")
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    npm run check
    if ($LASTEXITCODE -ne 0) { throw "svelte-check failed." }
    npm test
    if ($LASTEXITCODE -ne 0) { throw "UI tests failed." }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "UI build failed." }
} finally {
    Pop-Location
}

if (-not $SkipTests) {
    dotnet test (Join-Path $root "RappyRuns.sln") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$publish = Join-Path $dist "publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
$publishArgs = @((Join-Path $root "src\RappyRuns.App\RappyRuns.App.csproj"), "-c", "Release", "-o", $publish, "-p:SkipUiBuild=true")
if ($Version) { $publishArgs += "-p:ClientVersion=$Version" }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publish "RappyRunsClient.exe"
if (-not (Test-Path $exe)) { throw "Missing $exe after publish." }
# Anything besides the exe would be dropped by the Lisp updater, which copies
# only the exe, data\* and ffmpeg\*. Fail rather than ship an exe that
# depends on a file that never arrives.
$extra = Get-ChildItem $publish -File | Where-Object { $_.Name -ne "RappyRunsClient.exe" -and $_.Extension -ne ".pdb" }
if ($extra) { throw "publish produced files besides the exe: $($extra.Name -join ', ')" }

$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "data\pin-share") | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $client "data\quest-triggers.sexp") (Join-Path $stage "data")
Copy-Item (Join-Path $client "data\pin-share\init.lua") (Join-Path $stage "data\pin-share")

$stagedDll = Join-Path $stage "data\pin-share\pinshare-input.dll"
if ($InputDll) {
    Copy-Item $InputDll $stagedDll
} elseif (-not $SkipInputDll) {
    & (Join-Path $client "native\pinshare-input\build.cmd") $stagedDll
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $stagedDll)) { throw "Building pinshare-input.dll failed (needs the Visual Studio 2022 C++ tools; or pass -InputDll / -SkipInputDll)." }
}

$ffmpegDir = Join-Path $client "vendor\ffmpeg"
if (Test-Path (Join-Path $ffmpegDir "ffmpeg.exe")) {
    $license = Join-Path $ffmpegDir "LICENSE.txt"
    if (-not (Test-Path $license)) { throw "vendor\ffmpeg\ffmpeg.exe is present but LICENSE.txt is missing (GPL)." }
    New-Item -ItemType Directory -Force (Join-Path $stage "ffmpeg") | Out-Null
    Copy-Item (Join-Path $ffmpegDir "ffmpeg.exe"), $license (Join-Path $stage "ffmpeg")
} else {
    Write-Warning "client\vendor\ffmpeg\ffmpeg.exe not found - packaging WITHOUT the bundled ffmpeg."
}

$zip = Join-Path $dist "RappyRunsClient.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
# Compress-Archive like the Lisp package.ps1, so Expand-Archive on PS 5.1
# (the updater) reads it the same way.
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item -Recurse -Force $stage
Write-Host "Created $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
