; Inno Setup script for OKBoard. Build with installer\build.ps1 (it passes AppVersion and the exe path).
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppExe
  #define AppExe "..\publish\selfcontained\OKBoard.exe"
#endif

[Setup]
AppId={{8C1B6F2E-4A57-4E0B-9F3D-0B6C2B7E5A11}
AppName=OKBoard
AppVersion={#AppVersion}
AppVerName=OKBoard {#AppVersion}
AppPublisher=AlexG
AppPublisherURL=https://github.com/alexandergolubpl/OKBoard
AppSupportURL=https://github.com/alexandergolubpl/OKBoard/issues
AppUpdatesURL=https://github.com/alexandergolubpl/OKBoard/releases
DefaultDirName={autopf}\OKBoard
DisableProgramGroupPage=yes
; Per-user install into %LOCALAPPDATA%\Programs — no administrator rights needed.
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=OKBoard-Setup-{#AppVersion}
SetupIconFile=..\src\OKBoard\OKBoard.ico
UninstallDisplayIcon={app}\OKBoard.exe
UninstallDisplayName=OKBoard
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=OKBoard

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutoStart=Запускать OKBoard вместе с Windows
en.AutoStart=Start OKBoard with Windows
ru.OtherTasks=Дополнительно:
en.OtherTasks=Additional options:

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:OtherTasks}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:OtherTasks}"; Flags: unchecked

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; DestName: "OKBoard.exe"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\OKBoard"; Filename: "{app}\OKBoard.exe"
Name: "{autodesktop}\OKBoard"; Filename: "{app}\OKBoard.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OKBoard"; ValueData: """{app}\OKBoard.exe"""; Tasks: autostart

[Run]
Filename: "{app}\OKBoard.exe"; Description: "{cm:LaunchProgram,OKBoard}"; Flags: nowait postinstall skipifsilent
; Auto-update (silent install started by the app itself with /relaunch=1): start the new version again.
Filename: "{app}\OKBoard.exe"; Flags: nowait skipifnotsilent; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// The app lives in the tray (no main window), so close it explicitly before files are replaced/removed.
procedure CloseOKBoard;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im OKBoard.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseOKBoard;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseOKBoard;
    // Autostart may also have been enabled from inside the app.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OKBoard');
  end;
end;
