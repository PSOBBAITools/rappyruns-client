# Packages the delivered client into dist/RappyRunsClient.zip:
#   RappyRunsClient.exe
#   data/quest-triggers.sexp
#   data/pin-share/init.lua            (the Pin Share addon the client installs)
#   data/pin-share/pinshare-input.dll  (built here from native/pinshare-input; needs VS2022 C++)
#   ffmpeg/ffmpeg.exe + LICENSE.txt   (optional, from vendor/ffmpeg/ - see README)
# Run after building the exe with deliver.lisp (see README.md).
$ErrorActionPreference = "Stop"

$dist = Join-Path $PSScriptRoot "dist"
$exe = Join-Path $dist "RappyRunsClient.exe"
$triggers = Join-Path $PSScriptRoot "data\quest-triggers.sexp"
$pinShare = Join-Path $PSScriptRoot "data\pin-share\init.lua"
$ffmpegDir = Join-Path $PSScriptRoot "vendor\ffmpeg"

if (-not (Test-Path $exe)) { throw "Missing $exe - build it first (deliver.lisp)." }
if (-not (Test-Path $triggers)) { throw "Missing $triggers." }
if (-not (Test-Path $pinShare)) { throw "Missing $pinShare." }

$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "data") | Out-Null

Copy-Item $triggers (Join-Path $stage "data")
# Under data/ on purpose: already-deployed self-updaters copy data\*
# recursively and nothing else, so the addon reaches existing installs.
New-Item -ItemType Directory -Force (Join-Path $stage "data\pin-share") | Out-Null
Copy-Item $pinShare (Join-Path $stage "data\pin-share")
# Built fresh every time so the zip never carries a stale binary. Without it
# Pin Share still works, but bound keys also trigger the game's functions -
# a release must not silently lose that, hence a hard failure.
$inputDll = Join-Path $stage "data\pin-share\pinshare-input.dll"
& (Join-Path $PSScriptRoot "native\pinshare-input\build.cmd") $inputDll
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $inputDll)) { throw "Building pinshare-input.dll failed (needs the Visual Studio 2022 C++ tools)." }

# The client looks for ffmpeg/ffmpeg.exe next to its exe for the video
# recording feature. Bundling is optional: without it the zip still works,
# users just have to install ffmpeg themselves to record.
if (Test-Path (Join-Path $ffmpegDir "ffmpeg.exe")) {
    New-Item -ItemType Directory -Force (Join-Path $stage "ffmpeg") | Out-Null
    Copy-Item (Join-Path $ffmpegDir "ffmpeg.exe") (Join-Path $stage "ffmpeg")
    $license = Join-Path $ffmpegDir "LICENSE.txt"
    if (-not (Test-Path $license)) {
        throw "vendor\ffmpeg\ffmpeg.exe is present but LICENSE.txt is missing - a GPL ffmpeg build must ship with its license text."
    }
    Copy-Item $license (Join-Path $stage "ffmpeg")
} else {
    Write-Warning "vendor\ffmpeg\ffmpeg.exe not found - packaging WITHOUT the bundled ffmpeg (recording will need a user-installed ffmpeg). See README.md 'Bundling ffmpeg'."
}

Copy-Item $exe (Join-Path $stage "RappyRunsClient.exe")
$zip = Join-Path $dist "RappyRunsClient.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "Created $zip"

Remove-Item -Recurse -Force $stage
