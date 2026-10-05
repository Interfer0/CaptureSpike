# Fetches bundled ffmpeg + mpv builds into tools/<rid>/.
# Windows primary target. Run from the repo root: powershell -File scripts/fetch-tools.ps1

$ErrorActionPreference = "Stop"
$tools = Join-Path $PSScriptRoot ".." "tools" "win-x64"
New-Item -ItemType Directory -Force -Path $tools | Out-Null

function Get-Zip($url, $dest) {
    $tmp = Join-Path $env:TEMP ([System.IO.Path]::GetFileName($url))
    Write-Host "Downloading $url ..."
    Invoke-WebRequest -Uri $url -OutFile $tmp
    Expand-Archive -Path $tmp -DestinationPath $dest -Force
    Remove-Item $tmp
}

# ffmpeg (essentials build, gyan.dev) — check https://www.gyan.dev/ffmpeg/builds/ for current names
$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$tmpDir = Join-Path $env:TEMP "cs-ffmpeg"
Get-Zip $ffmpegUrl $tmpDir
$exe = Get-ChildItem -Path $tmpDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $tools "ffmpeg.exe")
# Ship the license text alongside the binary:
$license = Get-ChildItem -Path $tmpDir -Recurse -Filter "LICENSE*" | Select-Object -First 1
if ($license) { Copy-Item $license.FullName (Join-Path $tools "ffmpeg-LICENSE.txt") }
Remove-Item -Recurse -Force $tmpDir

# mpv — check https://mpv.io/installation/ for current Windows builds
$mpvUrl = "https://sourceforge.net/projects/mpv-player-windows/files/64bit/mpv-x86_64-20260201-git-38d7d63.7z/download"
Write-Host "NOTE: verify the mpv URL in this script is still current, then uncomment the lines below."
# $tmpDir = Join-Path $env:TEMP "cs-mpv"
# ... download + extract mpv.exe into $tools ...

Write-Host "Done. Binaries live in $tools (gitignored)."
