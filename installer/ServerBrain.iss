; Server Brain installer (Inno Setup 6). Build with:  scripts\build.ps1 -Installer
; Installs per-user by default (no admin rights, no UAC prompt). Choose "all users" in the first dialog if you prefer.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Server Brain"
#define AppExe "ServerBrain.exe"

[Setup]
AppId={{6F1B7C0E-2D54-4C0B-9B0E-5E3B9A7D4A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Server Brain contributors
AppCopyright=Copyright (c) 2026 Server Brain contributors. MIT License.
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\out
OutputBaseFilename=ServerBrain-Setup-{#AppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=*.exe
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows (recommended)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\out\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Start with Windows" = a per-user Run entry. It starts hidden in the tray, then starts your Always-on projects.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ServerBrain"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1B7C0E-2D54-4C0B-9B0E-5E3B9A7D4A21}_is1';
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  ModePage: TInputOptionWizardPage;
  OldVersion: String;
  HadData: Boolean;

function AppDataDir(): String;
var
  EnvDir: String;
begin
  { SB_DATA is the same override the app honours; it is only set when testing. }
  EnvDir := GetEnv('SB_DATA');
  if EnvDir <> '' then Result := EnvDir
  else Result := ExpandConstant('{localappdata}\ServerBrain');
end;

function FindOldVersion(): String;
var
  V: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', V) then Result := V
  else if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', V) then Result := V;
end;

function IsCleanRequested(): Boolean;
begin
  Result := (ExpandConstant('{param:CLEAN|0}') = '1');
end;

function NeedWebView2(): Boolean;
var
  V: String;
begin
  if ExpandConstant('{param:WV2TEST|0}') = '1' then begin Result := True; exit; end;
  Result := True;
  if RegQueryStringValue(HKLM32, WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKLM64, WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKCU, WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0') then Result := False;
end;

{ Ask a running copy to quit (it stops the projects it manages first) so files can be replaced / removed. }
procedure QuitRunningApp();
var
  ExePath: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#AppExe}');
  if FileExists(ExePath) then
  begin
    Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
end;

procedure InitializeWizard();
var
  Intro: String;
begin
  OldVersion := FindOldVersion();
  HadData := DirExists(AppDataDir());

  { Page shown only when Server Brain (or its data) is already on this PC: update or clean install. }
  if OldVersion <> '' then
    Intro := 'Server Brain ' + OldVersion + ' is already installed. This setup installs version {#AppVersion}.'
  else
    Intro := 'Settings and projects from an earlier Server Brain were found on this PC.';
  ModePage := CreateInputOptionPage(wpWelcome, 'Existing installation found', 'How do you want to install?',
    Intro + #13#10 + #13#10 + 'Your projects and settings are stored separately from the program in:' + #13#10 + AppDataDir(),
    True, False);
  ModePage.Add('Update - keep all my projects and settings (recommended)');
  ModePage.Add('Clean install - start fresh. My current setup is saved to a backup folder first.');
  ModePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = ModePage.ID then
    Result := (OldVersion = '') and (not HadData);
end;

function WantsCleanInstall(): Boolean;
begin
  Result := IsCleanRequested() or (((OldVersion <> '') or HadData) and (ModePage.SelectedValueIndex = 1));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Backup: String;
  Setup: String;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if WantsCleanInstall() and DirExists(AppDataDir()) then
    begin
      Backup := AppDataDir() + '.backup-' + GetDateTimeString('yyyymmdd-hhnnss', #0, #0);
      Log('Moving previous data to ' + Backup);
      Exec(ExpandConstant('{cmd}'), '/C move "' + AppDataDir() + '" "' + Backup + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if DirExists(Backup) then
        Log('Previous data moved to ' + Backup)
      else
        Log('Could not move previous data; it will be kept.');      RegDeleteValue(HKCU, RunKey, 'ServerBrain');   { the "start with Windows" task below decides again }
    end;
  end;
  if CurStep = ssPostInstall then
  begin
    { New PC without the Edge WebView2 runtime: fetch Microsoft's small installer so the app window works.
      If anything fails the app still works - it simply opens the dashboard in the browser. }
    if NeedWebView2() then
    begin
      Setup := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
      try
        WizardForm.StatusLabel.Caption := 'Downloading the Microsoft WebView2 runtime...';
        DownloadTemporaryFile(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
        WizardForm.StatusLabel.Caption := 'Installing the Microsoft WebView2 runtime...';
        Exec(Setup, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      except
        Log('WebView2 setup skipped: ' + GetExceptionMessage);
      end;
    end;
  end;
end;
function InitializeUninstall(): Boolean;
begin
  QuitRunningApp();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove the startup entry even if it was switched on from inside the app after install. }
    RegDeleteValue(HKCU, RunKey, 'ServerBrain');
    if DirExists(AppDataDir()) then
    begin
      if not UninstallSilent then
      begin
        if MsgBox('Also delete your Server Brain settings, project list and logs?' + #13#10 + #13#10 +
                  'Your projects themselves (the folders you added) are never touched.' + #13#10 +
                  'Choose No to keep them in case you reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
          DelTree(AppDataDir(), True, True, True);
      end;
    end;
  end;
end;





