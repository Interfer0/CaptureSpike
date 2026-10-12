; CaptureSpike Windows installer.
;
; Per-user install (no UAC prompt) into %LOCALAPPDATA%\CaptureSpike.
; Self-contained: ffmpeg + mpv binaries are bundled from
; tools/win-x64/, the .NET runtime is self-contained
; (--self-contained true at publish time), so the user
; does not need to install .NET.
;
; Build with:
;   powershell -File installer/windows/build-installer.ps1
; which:
;   1. dotnet publish -c Release -r win-x64 --self-contained
;      -o dist/publish/win-x64
;   2. Runs iscc on this script to produce
;      dist/CaptureSpike-Setup-<version>.exe
;
; AppId, AppVersion, OutputBaseFilename are pre-baked here;
; publish rewrites AppVersion on each build via /DAPP_VERSION
; (see build-installer.ps1).

#define MyAppName "CaptureSpike"
#define MyAppPublisher "Daniel Bigelow"
#define MyAppURL "https://github.com/Interfer0/CaptureSpike"
#ifndef APP_VERSION
  #define APP_VERSION "0.0.0-dev"
#endif
#define MyAppExeName "CaptureSpike.App.exe"

[Setup]
; AppId is unique to this app. Don't change it; the GUID
; identifies the install to Add/Remove Programs so an upgrade
; overwrites the previous install rather than installing
; alongside it.
AppId={{B9F7E7A4-1D2C-4E0F-8B5D-3E2A6C7F1A2B}
AppName={#MyAppName}
AppVersion={#APP_VERSION}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autolocalappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The user picks install location. Default is %LOCALAPPDATA%
; but they can change it to anywhere they have write access
; (e.g. a USB drive, D:\Tools\CaptureSpike). Per-user
; install — no admin elevation needed.
PrivilegesRequired=lowest
; Per Inno Setup docs (jrsoftware.org/ishelp), the
; directive is PrivilegesRequiredOverridesAllowed
; (with trailing s) in both 6.x and 7.x. The 7.x
; compiler rejects the no-s form. The setting lets
; the user override the 'lowest' default via a
; dialog ("Install for all users" vs "Install for
; just me") or via /ALLUSERS and /CURRENTUSER
; command-line parameters.
PrivilegesRequiredOverridesAllowed=dialog
; Output goes to dist/ so it stays out of the repo (.gitignore).
OutputDir=..\..\dist
OutputBaseFilename=CaptureSpike-Setup-{#APP_VERSION}
; Modern UI: install wizard, not the old wizard-from-2003.
WizardStyle=modern
; Show the GPL-3.0 license text on the License page
; before the user clicks "I agree". This is required
; for the GPL to be legally valid in some jurisdictions
; (the user must have the opportunity to read the
; license terms before installing).
;
; The LICENSE.txt file is the standard FSF GPL-3.0
; text, kept plain (Windows-1252 compatible, no
; Markdown) so Inno Setup can display it without
; formatting glitches. It's checked into the repo
; alongside this script.
LicenseFile=LICENSE.txt
;
; SetupIconFile= points at the multi-resolution .ico
; built by the BuildWindowsIco target (ffmpeg + Python).
; The .ico is checked into the repo so the build is
; reproducible. To regenerate after a PNG change:
;   1. Delete src/CaptureSpike.App/Assets/capturespike-icon.ico
;   2. Rebuild
SetupIconFile=..\..\src\CaptureSpike.App\Assets\capturespike-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
; Vista+ for 64-bit support; the app itself is AnyCPU but
; we're producing x64 binaries.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; PrivilegesRequiredOverridesAllowed=dialog (above)
; lets the user override the 'lowest' default via a
; dialog if they want to install for all users.
Uninstallable=yes
VersionInfoVersion={#APP_VERSION}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Add to PATH on install? Most users won't need it; ffmpeg
; is invoked via ToolPaths which prefers the bundled copy.
; The PATH task is a checkbox off by default.

[Files]
; The publish output. /标志 `recursesubdirs` so the entire
; win-x64\ folder (including ffmpeg.exe, mpv.exe, *.dll,
; .NET runtime) goes under {app}\.
Source: "..\..\dist\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Third-party licenses are required by ffmpeg/mpv licenses;
; the .NET runtime's MIT license is included by the SDK
; but we ship our own bundled-licenses file too.
Source: "..\..\tools\THIRD-PARTY-LICENSES.md"; DestDir: "{app}"; Flags: ignoreversion
; The GPL-3.0 license text is shown in the installer
; wizard (LicenseFile= above) but we also install a
; copy next to the .exe so the user can read it later
; without re-running the installer.
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; The project NOTICE file (copyright + bundled
; attributions) is required by GPL-3.0 section 5
; (preservation of attribution when the program
; is modified and redistributed). Install it
; alongside the LICENSE.txt.
Source: "..\..\NOTICE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu entry. Per-user installs go under
; {userstartmenu} which is per-user, not the All Users
; start menu.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; On uninstall, remove the entire {app} folder. By default
; Inno Setup only removes files it knows about; this
; catches anything else (logs, cached icc profiles,
; user's ffmpeg/mpv binaries if they were updated).
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\tools"
; We do NOT remove the user's projects folder (AppSettings
; defaults to %USERPROFILE%\Videos\CaptureSpike) — that's
; the user's data, and deleting it on uninstall would be
; destructive.
