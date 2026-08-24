; Dockyard installer — Inno Setup 6
;
; Per-user by default: installs to %LOCALAPPDATA%\Programs\Dockyard, needs no admin rights,
; and can be elevated to all-users from the dialog if someone wants that.
;
; Built by publish-release.bat. Expects ..\release\Dockyard.exe to already exist.

#define AppName        "Dockyard"
#define AppVersion     "1.1.0"
#define AppPublisher   "Niko Huebert"
#define AppExeName     "Dockyard.exe"
#define AppUrl         "https://github.com/ZenCodeOrSomeShit/dockyard"

[Setup]
AppId={{7B3F9C41-6E2A-4D58-9C1E-DA0B4F72E9A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases

; Per-user install by default; the dialog can still offer all-users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

OutputDir=..\release
OutputBaseFilename=DockyardSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; The dock holds its own exe open, so shut it down before overwriting.
CloseApplications=yes
CloseApplicationsFilter=Dockyard.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup";     Description: "Start {#AppName} when I sign in"

[Files]
Source: "..\release\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Matches what the app's own "Start with Windows" toggle writes, so the two agree
; rather than fighting over the same value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "Dockyard"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure it isn't running before we pull the files out from under it.
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#AppExeName}"; \
    Flags: runhidden; RunOnceId: "StopDockyard"

[Code]
// Settings live outside the install folder, so they survive an uninstall unless
// the user says otherwise. Asking beats silently keeping or silently deleting.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ConfigDir := ExpandConstant('{userappdata}\Dockyard');
    if DirExists(ConfigDir) then
    begin
      if MsgBox('Remove your Dockyard settings as well?' + #13#10 + #13#10 +
                'This deletes your tiles, colours and layout from:' + #13#10 +
                ConfigDir + #13#10 + #13#10 +
                'Choose No to keep them for a reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ConfigDir, True, True, True);
    end;
  end;
end;
