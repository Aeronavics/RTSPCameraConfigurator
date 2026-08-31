; ---------------------------------------------------------------------------
;  Camera Setup - Inno Setup script
;
;  Build with:  installer\build-installer.cmd
;  or directly: ISCC.exe /DPayload="..\dist" installer\CameraSetup.iss
;
;  WHY THIS INSTALLS PER-USER, NOT INTO PROGRAM FILES
;
;  The app reads and writes cameras.json and the presets folder from its own
;  directory (AppContext.BaseDirectory): Settings -> Subnets rewrites
;  cameras.json in place, and Export writes parameter files into presets\.
;  Under Program Files a standard user can do neither - saving settings and
;  exporting a preset would both fail with an access error.
;
;  Installing to %LOCALAPPDATA%\Programs keeps that model working, and needs no
;  administrator rights. Change PrivilegesRequired only if the app is changed to
;  keep its data elsewhere.
; ---------------------------------------------------------------------------

#ifndef Payload
  #define Payload "..\dist"
#endif

#define AppName        "RTSP Camera Setup"
#define AppExe         "RTSPCameraConfigurator.exe"
#define AppPublisher   "Aeronavics"
#define AppUrl         "https://github.com/Aeronavics/RTSPCameraConfigurator"

; Version is read from the built executable so it cannot drift from the binary.
#define AppVersion GetVersionNumbersString(AddBackslash(Payload) + AppExe)
#if AppVersion == ""
  #define AppVersion "1.0.0.0"
#endif

[Setup]
; Never change AppId: it is what lets a later build upgrade this one in place.
AppId={{8F3C4A21-6D9E-4B77-9C2A-0E51B7A4D6C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

DefaultDirName={localappdata}\Programs\RTSPCameraConfigurator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Per-user: no UAC prompt, and the install folder stays writable by the app.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; The payload is a self-contained .NET publish plus the libvlc tree, so it is
; x64-only and large. LZMA2 with a big dictionary takes the ~370 MB down a long way.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

OutputDir=..\build
OutputBaseFilename=RTSPCameraConfigurator-Setup-{#AppVersion}
SetupIconFile=..\src\RTSPCameraConfigurator\assets\app.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Everything except the two things the user owns. The whole folder ships: the app
; finds ffmpeg.exe beside itself, which is why it is not published single-file.
Source: "{#Payload}\*"; DestDir: "{app}"; \
    Excludes: "cameras.json,presets\*,*.pdb"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; cameras.json is edited in place by the app, so an upgrade must not overwrite the
; user's watched subnets and tuning.
Source: "{#Payload}\cameras.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

; ...but ship the current one alongside it, always refreshed, so after an upgrade
; there is something to diff against when new settings appear.
Source: "{#Payload}\cameras.json"; DestDir: "{app}"; DestName: "cameras.reference.json"; Flags: ignoreversion

; Presets are user data. Seed the shipped one on a clean install and never touch
; anything in that folder again.
Source: "{#Payload}\presets\Generic.json"; DestDir: "{app}\presets"; Flags: onlyifdoesntexist uninsneveruninstall

; ffmpeg and its licence are already in the payload: the publish bundles them,
; so a published folder is a complete app on its own and the installer has no
; special case for them.

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leaves cameras.json and presets\ behind deliberately - they are the user's
; configuration and captured cameras, not ours to delete.
Type: dirifempty; Name: "{app}"
