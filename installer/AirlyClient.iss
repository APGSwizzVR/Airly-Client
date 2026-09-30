#define MyAppName "Airly Client"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Airly"
#define MyAppExeName "AirlyClient.exe"

[Setup]
AppId={{A1E1D7B8-6E0A-4A7C-8A0A-4A7E4A1A1F01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Airly Client
DefaultGroupName=Airly Client
OutputDir=installer-output
OutputBaseFilename=Airly-Client-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\publish\AirlyClient.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Airly Client"; Filename: "{app}\AirlyClient.exe"
Name: "{autodesktop}\Airly Client"; Filename: "{app}\AirlyClient.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start Airly Client with Windows"; GroupDescription: "Windows startup:"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AirlyClient"; ValueData: "{app}\AirlyClient.exe"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Open Airly Client"; Flags: nowait postinstall skipifsilent
