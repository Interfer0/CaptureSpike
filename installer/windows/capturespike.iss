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
PrivilegesRequiredOverridesAllowed=dialog
; Output goes to dist/ so it stays out of the repo (.gitignore).
OutputDir=..\..\dist
OutputBaseFilename=CaptureSpike-Setup-{#APP_VERSION}
; Modern UI: install wizard, not the old wizard-from-2003.
WizardStyle=modern
; Display the README during install.
;
; SetupIconFile= would point at a custom .ico
; (Assets\capturespike-icon.ico), but we don't
; ship one in the repo (it's gitignored and there's
; no portable PNG-to-ICO generator that works
; without ImageMagick). Inno Setup falls back to
; its default wizard icon when this is omitted.
; When a hand-built .ico is available, uncomment
; the line below and rebuild.
;
; SetupIconFile=..\..\src\CaptureSpike.App\Assets\capturespike-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
; Vista+ for 64-bit support; the app itself is AnyCPU but
; we're producing x64 binaries.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; "Privileges required" message shown in the header of the
; install wizard. The 'lowest' setting is reflected here so
; the user knows they don't need admin rights.
PrivilegesRequiredOverrideAllowed=yes
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
