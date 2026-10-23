; AirWave installer (Inno Setup 6). Built by GitHub Actions: iscc /DAppVersion=x.y.z /DSourceExe=path\AirWave.exe installer\AirWave.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\AirWave.exe"
#endif

[Setup]
AppId={{7E1C2B6A-4D0F-4B8E-9C5A-6A1E0F3B2D91}
AppName=AirWave
AppVersion={#AppVersion}
AppVerName=AirWave {#AppVersion}
AppPublisher=AirWave
AppPublisherURL=https://github.com/AggyMarmite/AirWave
AppSupportURL=https://github.com/AggyMarmite/AirWave/issues
DefaultDirName={autopf}\AirWave
DefaultGroupName=AirWave
DisableProgramGroupPage=yes
DisableDirPage=yes
; Installs just for the current user, no admin prompt (can be changed in the setup dialog)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=AirWave-Setup-{#AppVersion}
SetupIconFile=..\src\AirWave\Assets\AirWave.ico
UninstallDisplayIcon={app}\AirWave.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running AirWave before updating it
AppMutex=AirWave.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AirWave"; Filename: "{app}\AirWave.exe"
Name: "{autodesktop}\AirWave"; Filename: "{app}\AirWave.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AirWave.exe"; Description: "Open AirWave"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer quietly with /RELAUNCH=1: start AirWave again afterwards
Filename: "{app}\AirWave.exe"; Parameters: "--after-update"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Registry]
; Remove the "start with Windows" entry the app may have added
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "AirWave"; Flags: uninsdeletevalue dontcreatekey

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AirWave\log.txt"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
