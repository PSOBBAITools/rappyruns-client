# Builds, packages and publishes the C# client (issue #322).
#   .\desktop\release.ps1 v0.90.0 -Dogfood            # test release to the dogfood repo
#   .\desktop\release.ps1 v1.0.0 -NotesFile notes.md  # the real switch (P8): every client updates
#   .\desktop\release.ps1 v0.90.0 -Dogfood -Clobber   # replace the asset on an existing release
#
# Dogfood releases go to a separate PUBLIC repo as ordinary (non-pre)releases,
# because the updater reads /releases/latest, which skips prereleases. Testers
# point their client at it with (:UPDATE-REPO "PSOBBAITools/rappyruns-client-dogfood")
# in %APPDATA%\ephinea-ta-client\config.sexp; the Lisp bridge client (v0.61+)
# then installs the C# client through the same path every user takes at P8.
#
# Requires the .NET 10 SDK, Node 24, the VS2022 C++ tools (pinshare-input.dll),
# client\vendor\ffmpeg (bundled ffmpeg + LICENSE) and an authenticated `gh`.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$NotesFile,
    [switch]$Dogfood,
    [switch]$Clobber
)
$ErrorActionPreference = "Stop"

# A gh call whose failure is an answer ("no such repo"). Under "Stop",
# Windows PowerShell 5.1 turns the stderr of a native command whose stream is
# redirected (2>$null) into a terminating NativeCommandError, so the exit code
# would never be seen: run it under "Continue" and hand back output + exit code.
function Invoke-GhQuiet {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & gh @args 2>$null
        [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    } finally {
        $ErrorActionPreference = $previous
    }
}

$mainRepo = "PSOBBAITools/rappyruns-client"
$dogfoodRepo = "PSOBBAITools/rappyruns-client-dogfood"
$repo = if ($Dogfood) { $dogfoodRepo } else { $mainRepo }

if ($Version -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Version must look like v1.2.3 (got: $Version)." }
$bare = $Matches[1]
$fileVersion = (Get-Content (Join-Path $PSScriptRoot "VERSION") -TotalCount 1).Trim()
if ($fileVersion -ne $bare) {
    throw "desktop/VERSION ($fileVersion) does not match the release tag ($Version); bump and commit desktop/VERSION first."
}
# The Lisp updater only installs a strictly newer X.Y.Z (spec core §10.7 #1).
if (-not $Dogfood) {
    $latest = Invoke-GhQuiet release view --repo $mainRepo --json tagName -q .tagName
    $latestLisp = "$($latest.Output)".Trim()
    if ($latest.ExitCode -ne 0 -or $latestLisp -notmatch '^v\d+\.\d+\.\d+$') {
        throw "Could not read the latest release of $mainRepo (gh exit $($latest.ExitCode)): '$latestLisp'."
    }
    if ([version]$bare -le [version]($latestLisp.TrimStart('v'))) {
        throw "$Version is not newer than the current release $latestLisp."
    }
}
if (-not (Test-Path (Join-Path $PSScriptRoot "..\client\vendor\ffmpeg\ffmpeg.exe"))) {
    throw "client\vendor\ffmpeg\ffmpeg.exe is missing: a release must bundle ffmpeg (see client/README.md)."
}

& (Join-Path $PSScriptRoot "package.ps1") -Version $bare
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { throw "package.ps1 failed." }
$zip = Join-Path $PSScriptRoot "dist\RappyRunsClient.zip"
if (-not (Test-Path $zip)) { throw "Missing $zip." }

# Publish the source BEFORE the release so its tag lands on the exact commit
# the exe was built from (the Lisp release does the same with client/ on main).
if (-not $Dogfood) {
    & (Join-Path $PSScriptRoot "..\scripts\publish-desktop-source.ps1")
}

if ($Dogfood) {
    if ((Invoke-GhQuiet repo view $dogfoodRepo --json name).ExitCode -ne 0) {
        gh repo create $dogfoodRepo --public --add-readme --description "Test releases of the Rappy Runs C# client (not for general use)"
        if ($LASTEXITCODE -ne 0) { throw "could not create $dogfoodRepo" }
    }
}

if ($Clobber) {
    gh release upload $Version $zip --clobber --repo $repo
} else {
    $ghArgs = @("release", "create", $Version, $zip, "--repo", $repo, "--title", "Rappy Runs Client $Version")
    if ($NotesFile) { $ghArgs += @("--notes-file", $NotesFile) }
    else { $ghArgs += @("--notes", "Rappy Runs Client $Version.") }
    # A dogfood repo has no source; give the tag something to point at. A real
    # release is tagged on the mirrored C# source (publish-desktop-source.ps1).
    if ($Dogfood) { $ghArgs += @("--target", "main") }
    else { $ghArgs += @("--target", "csharp") }
    gh @ghArgs
}
if ($LASTEXITCODE -ne 0) { throw "gh failed (exit $LASTEXITCODE)." }
Write-Host "Published $Version to https://github.com/$repo/releases"
