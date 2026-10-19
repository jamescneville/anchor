; Anchor installer (Inno Setup 6). Build with scripts\build-installer.ps1, which publishes dist\Anchor.exe first.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6B1F3C52-7D0A-4C0E-9B53-A9C0E3D1A4F7}
AppName=Anchor
AppVersion={#AppVersion}
AppVerName=Anchor {#AppVersion}
AppPublisher=James Neville
DefaultDirName={autopf}\Anchor
DefaultGroupName=Anchor
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Anchor.exe
UninstallDisplayName=Anchor
SetupIconFile=..\src\Anchor.Desktop\Assets\anchor.ico
OutputDir=..\dist
OutputBaseFilename=Anchor-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
WizardStyle=modern
; Close a running Anchor during install/uninstall (the app holds this mutex while it runs).
AppMutex=Anchor.SingleInstance
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start Anchor when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "..\dist\Anchor.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Anchor"; Filename: "{app}\Anchor.exe"
Name: "{group}\Uninstall Anchor"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Anchor"; Filename: "{app}\Anchor.exe"; Tasks: desktopicon

[Registry]
; Per-user autostart for whoever ran setup. Anchor's tray menu ("Start with Windows") can switch it later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Anchor"; ValueData: """{app}\Anchor.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Anchor.exe"; Description: "Start Anchor now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Anchor.exe /F"; Flags: runhidden; RunOnceId: "StopAnchor"

[Code]
// Settings, calendar/mail tokens and state live in %LOCALAPPDATA%\Anchor and survive an update. On uninstall, ask before removing them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Anchor');
    if DirExists(DataDir) then
      if MsgBox('Also delete your Anchor settings, sign-in tokens and saved state?' + #13#10 + #13#10 + DataDir + #13#10 + #13#10 +
                'Choose No to keep them (for example if you plan to reinstall).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
