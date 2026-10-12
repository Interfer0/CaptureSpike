# Build the Windows installer.
#
#   powershell -File installer/windows/build-installer.ps1
#
# Steps:
#   1. Fetch Inno Setup (cached in tools/inno-setup/) if not
#      already present. Re-uses the existing install if
#      iscc.exe is there.
#   2. Fetch bundled ffmpeg + mpv (cached in tools/win-x64/)
#      if not already present. The Inno Setup script
#      references the publish output, so this MUST run
#      before dotnet publish.
#   3. dotnet publish self-contained to dist/publish/win-x64.
#      The publish target also emits the capturespike-icon.ico
#      from the PNG (Windows expects a multi-resolution .ico
#      for the installer icon and Start Menu icon).
#   4. Run iscc with /DAPP_VERSION= from the project's
#      version (AssemblyInfo). The .iss file references
#      dist/ as its output directory; the resulting
#      `dist\CaptureSpike-Setup-<version>.exe` is what
#      users run.
#
# Everything lands under dist/ which is gitignored. The
# installer is the only artifact you ship.

$ErrorActionPreference = "Stop"
function Join-Many([string[]]$parts) { [System.IO.Path]::Combine($parts) }
$repoRoot = Join-Many @($PSScriptRoot, "..", "..")
. (Join-Many @($repoRoot, "scripts", "fetch-tools.ps1"))

# --- Step 1: Inno Setup ---
Write-Host "==> Step 1/4: Inno Setup"
$innoDir = Fetch-InnoSetup
$iscc = Join-Many @($innoDir, "iscc.exe")
if (-not (Test-Path $iscc)) { throw "iscc.exe not found at $iscc" }

# --- Step 2: ffmpeg + mpv (bundled runtime tools) ---
# Source the script (NOT execute) so the Fetch-InnoSetup
# function is loaded into this scope and the side-effect
# of the top-level code (ffmpeg + mpv download) runs here.
Write-Host "==> Step 2/4: Bundled ffmpeg + mpv"
. (Join-Many @($repoRoot, "scripts", "fetch-tools.ps1"))
$winTools = Join-Many @($repoRoot, "tools", "win-x64")
if (-not (Test-Path (Join-Path $winTools "ffmpeg.exe"))) { throw "ffmpeg.exe missing from $winTools after fetch" }
if (-not (Test-Path (Join-Path $winTools "mpv.exe"))) { throw "mpv.exe missing from $winTools after fetch" }

# --- Step 3: dotnet publish (self-contained) ---
Write-Host "==> Step 3/4: dotnet publish self-contained"
$publishDir = Join-Many @($repoRoot, "dist", "publish", "win-x64")
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
# The csproj has two TFMs (net10.0 for Linux, net10.0-windows*
# for Windows). Without -f, dotnet publish errors with
# NETSDK1129. The Windows TFM is what the installer
# bundles; the Linux TFM is for the portable Linux
# publish workflow (Publishing Builds section in README).
& dotnet @("publish",
    (Join-Many @($repoRoot, "src", "CaptureSpike.App")),
    "-c", "Release",
    "-f", "net10.0-windows10.0.19041",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:WindowsAppSDKSelfContained=true",
    "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $publishDir
)
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }

# Copy the bundled tools into the publish output. The
# app's ToolPaths prefers the bundled tools/<rid>/ over
# a system ffmpeg/mpv, so they MUST end up in the same
# folder as the .exe. We put them under
# dist\publish\win-x64\tools\win-x64\ so the relative
# path ToolPaths computes from AppContext.BaseDirectory
# resolves correctly.
$bundledTools = Join-Many @($publishDir, "tools", "win-x64")
New-Item -ItemType Directory -Force -Path $bundledTools | Out-Null
Copy-Item (Join-Path $winTools "*") $bundledTools -Recurse -Force

# --- Step 4: iscc ---
Write-Host "==> Step 4/4: iscc"

# Read the version from the .csproj. The .iss file uses
# {#APP_VERSION} for AppVersion and the output filename
# (so the alpha suffix is preserved), and
# {#APP_VERSION_NUMERIC} for VersionInfoVersion which
# Inno Setup requires to be just numbers and dots.
$csproj = Get-Content (Join-Many @($repoRoot, "src", "CaptureSpike.App", "CaptureSpike.App.csproj")) -Raw
$versionMatch = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
$appVersion = if ($versionMatch.Success) { $versionMatch.Groups[1].Value } else { "0.0.0-dev" }

# Strip the pre-release suffix for VersionInfoVersion.
# "0.1.0-alpha.1" -> "0.1.0", "1.2.3-rc.4" -> "1.2.3".
# SemVer pre-release identifiers come after the first '-'.
$numericMatch = [regex]::Match($appVersion, '^(\d+(?:\.\d+){0,3})')
$appVersionNumeric = if ($numericMatch.Success) { $numericMatch.Groups[1].Value } else { "0.0.0" }

& $iscc /DAPP_VERSION=$appVersion /DAPP_VERSION_NUMERIC=$appVersionNumeric (Join-Many @($PSScriptRoot, "capturespike.iss"))
if ($LASTEXITCODE -ne 0) { throw "iscc failed with code $LASTEXITCODE" }

$installer = Join-Many @($repoRoot, "dist", "CaptureSpike-Setup-$appVersion.exe")
if (-not (Test-Path $installer)) {
    throw "Installer was not produced at expected path: $installer"
}

# SHA256 next to the installer. GitHub Releases can display
# the hash automatically (if the .sha256 file is uploaded as
# a release asset), so users can verify their download.
$sha256Path = "$installer.sha256"
$hash = (Get-FileHash -Path $installer -Algorithm SHA256).Hash
Set-Content -Path $sha256Path -Value "$hash  $(Split-Path $installer -Leaf)" -NoNewline
Write-Host "SHA256: $hash"

Write-Host ""
Write-Host "==> Done. Installer: $installer"
Write-Host "    Size: $([math]::Round((Get-Item $installer).Length / 1MB, 1)) MB"
Write-Host "    Hash: $sha256Path"
