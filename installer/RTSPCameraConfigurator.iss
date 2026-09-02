; ---------------------------------------------------------------------------
;  Camera Setup - Inno Setup script
;
;  Build with:  installer\build-installer.cmd
;  or directly: ISCC.exe /DPayload="..\dist" installer\CameraSetup.iss
;
;  WHY THIS INSTALLS PER-USER, NOT INTO PROGRAM FILES
;
;  The app writes the presets folder inside its own directory
;  (AppContext.BaseDirectory): Export writes parameter files into presets\.
;  Under Program Files a standard user could not, so exporting a preset would
;  fail with an access error. Settings are no longer written here at all - they
;  go to settings.json under %LOCALAPPDATA%.
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
; No PrivilegesRequiredOverridesAllowed: an all-users install lands in Program
; Files, where the app cannot write its own presets folder, so offering the
; choice only offers a broken one.
PrivilegesRequired=lowest

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

; cameras.json is program data - camera profiles, login-page signatures and the field
; definitions the UI is generated from - so an upgrade MUST replace it. Leaving it alone
; once meant a build shipped a new camera profile that no existing install ever saw.
; The operator's own choices are not in here: they live in settings.json under
; %LOCALAPPDATA%, which the app overlays on this file at startup.
Source: "{#Payload}\cameras.json"; DestDir: "{app}"; Flags: ignoreversion

; Presets are user data. Seed the shipped one on a clean install and never touch
; anything in that folder again.
Source: "{#Payload}\presets\Generic.json"; DestDir: "{app}\presets"; Flags: onlyifdoesntexist uninsneveruninstall

; ffmpeg and its licence are already in the payload: the publish bundles them,
; so a published folder is a complete app on its own and the installer has no
; special case for them.

[InstallDelete]
; Remove what earlier versions installed and this one no longer ships. Inno only
; tracks files it installs, so without this an upgrade LAYERS the new build over
; the old: upgrading a v1.0 install left its 198 MB libvlc tree in place and the
; folder grew to 485 MB.
Type: filesandordirs; Name: "{app}\libvlc"
Type: files; Name: "{app}\LibVLCSharp.dll"
Type: files; Name: "{app}\LibVLCSharp.WPF.dll"

; The executable was renamed, so the old one would otherwise linger and keep
; working from stale shortcuts.
Type: files; Name: "{app}\CameraSetup.exe"
Type: files; Name: "{app}\CameraSetup.dll"
Type: files; Name: "{app}\CameraSetup.deps.json"
Type: files; Name: "{app}\CameraSetup.runtimeconfig.json"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Code]
{ An older build kept the operator's watched subnets inside cameras.json, which this
  installer now replaces. Take a copy first so the app can lift those choices into
  settings.json on its next start.

  Only while that move is still owed: once settings.json exists the copy would serve no
  purpose, and taking it anyway would let a second upgrade overwrite the original with a
  file that is already ours. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing, Backup, Settings: String;
begin
  if CurStep = ssInstall then
  begin
    Existing := ExpandConstant('{app}\cameras.json');
    Backup   := ExpandConstant('{app}\cameras.previous.json');
    Settings := ExpandConstant('{localappdata}\RTSPCameraConfigurator\settings.json');

    if FileExists(Existing) and (not FileExists(Settings)) then
      FileCopy(Existing, Backup, False);
  end;
end;

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Leaves presets\ behind deliberately - those are the user's captured cameras,
; not ours to delete. Their settings live outside the install folder and are
; likewise untouched. cameras.json ships with the build, so it goes.
Type: dirifempty; Name: "{app}"
