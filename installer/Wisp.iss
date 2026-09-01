#define MyAppName "Wisp"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "Wisp"
#define MyAppExeName "Wisp.exe"

[Setup]
AppId={{A8FC0D58-11E3-4B25-B78D-3B98E9855473}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Wisp
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\outputs
OutputBaseFilename=Wisp-Setup-{#MyAppVersion}
SetupIconFile=..\src\Wisp.App\Assets\Wisp.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=Wisp installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WispUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8FC0D58-11E3-4B25-B78D-3B98E9855473}_is1';

var
  UpdatingExistingInstallation: Boolean;

function SetupRequiredMarkerPath(): String;
begin
  Result := ExpandConstant('{localappdata}\Wisp\setup-required');
end;

function UpdateSwitchPresent(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    if CompareText(ParamStr(Index), '/WISPUPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ExistingInstallationPresent(): Boolean;
var
  UninstallCommand: String;
begin
  Result := RegQueryStringValue(
    HKCU,
    WispUninstallKey,
    'UninstallString',
    UninstallCommand) and (UninstallCommand <> '');
end;

function InitializeSetup(): Boolean;
begin
  { Capture this before Setup creates or refreshes its uninstall entry. }
  UpdatingExistingInstallation := UpdateSwitchPresent() and ExistingInstallationPresent();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsDirectory: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  { A verified in-place update preserves the completed setup record. A fresh
    install, including one given /WISPUPDATE without an existing registration,
    still has to pass through the setup wizard. }
  if UpdatingExistingInstallation then
    Exit;

  SettingsDirectory := ExpandConstant('{localappdata}\Wisp');
  if not ForceDirectories(SettingsDirectory) then
    RaiseException('Wisp could not create its local settings directory.');
  if not SaveStringToFile(SetupRequiredMarkerPath(), 'setup-required', False) then
    RaiseException('Wisp could not create its setup requirement marker.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Wisp');
end;
