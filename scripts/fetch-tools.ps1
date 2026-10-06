# Fetches bundled ffmpeg + mpv builds into tools/win-x64/.
# Run from the repo root: powershell -File scripts/fetch-tools.ps1
# Binaries are gitignored; the app finds them via ToolPaths (bundled tools/<rid>/ first).

$ErrorActionPreference = "Stop"
function Join-Many([string[]]$parts) { [System.IO.Path]::Combine($parts) }
$repoRoot = Join-Many @($PSScriptRoot, "..")
$tools = Join-Many @($repoRoot, "tools", "win-x64")
New-Item -ItemType Directory -Force -Path $tools | Out-Null

# --- ffmpeg (gyan.dev essentials build; includes gdigrab + dshow capture) ---
$ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
$tmpDir = Join-Path $env:TEMP "cs-ffmpeg"
if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir }
Write-Host "Downloading ffmpeg ..."
Invoke-WebRequest -Uri $ffmpegUrl -OutFile (Join-Path $env:TEMP "cs-ffmpeg.zip")
Expand-Archive -Path (Join-Path $env:TEMP "cs-ffmpeg.zip") -DestinationPath $tmpDir -Force
Remove-Item (Join-Path $env:TEMP "cs-ffmpeg.zip")
$exe = Get-ChildItem -Path $tmpDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $tools "ffmpeg.exe")
$license = Get-ChildItem -Path $tmpDir -Recurse -Filter "LICENSE*" | Select-Object -First 1
if ($license) { Copy-Item $license.FullName (Join-Path $tools "ffmpeg-LICENSE.txt") }
Remove-Item -Recurse -Force $tmpDir

# --- mpv (shinchiro builds, recommended on https://mpv.io/installation/) ---
# The player archive is .7z, so fetch the 7-Zip console extractor (single exe, no install).
$sevenZr = Join-Path $env:TEMP "cs-7zr.exe"
if (-not (Test-Path $sevenZr)) {
    Write-Host "Downloading 7zr.exe ..."
    Invoke-WebRequest -Uri "https://www.7-zip.org/a/7zr.exe" -OutFile $sevenZr
}

# Resolve the latest non-v3 build via the GitHub API instead of hardcoding a dated URL.
# (The -v3 builds need AVX2 CPUs; the plain x86_64 build runs everywhere.)
Write-Host "Resolving latest mpv build ..."
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest"
$asset = $rel.assets | Where-Object { $_.name -like "mpv-x86_64-*.7z" -and $_.name -notlike "*-v3-*" } | Select-Object -First 1
if (-not $asset) { throw "No mpv-x86_64 player asset found in the latest shinchiro release." }
Write-Host "mpv asset: $($asset.name)"

$tmpDir = Join-Path $env:TEMP "cs-mpv"
if (Test-Path $tmpDir) { Remove-Item -Recurse -Force $tmpDir }
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$archive = Join-Path $tmpDir $asset.name
Write-Host "Downloading mpv ..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
& $sevenZr x $archive "-o$tmpDir" -y | Out-Null
$exe = Get-ChildItem -Path $tmpDir -Recurse -Filter "mpv.exe" | Select-Object -First 1
if (-not $exe) { throw "mpv.exe not found inside $($asset.name)." }
Copy-Item $exe.FullName (Join-Path $tools "mpv.exe")
# mpv is GPL-2.0-or-later: keep a copy of the source offer next to the binary.
Copy-Item (Join-Many @($repoRoot, "tools", "THIRD-PARTY-LICENSES.md")) (Join-Path $tools "THIRD-PARTY-LICENSES.md")
Remove-Item -Recurse -Force $tmpDir

Write-Host "Done. Binaries live in $tools (gitignored)."
Write-Host "Verify: & '$tools\mpv.exe' --version ; & '$tools\ffmpeg.exe' -version"
