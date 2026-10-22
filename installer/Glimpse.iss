#ifndef AppVersion
  #define AppVersion "0.1.0-preview.1"
#endif
#ifndef PayloadDir
  #error PayloadDir must be specified by tools/build_release.py
#endif
#ifndef AssetDir
  #error AssetDir must be specified by tools/build_release.py
#endif

[Setup]
AppId={{037BC922-BA57-42A8-B36B-6614D02E5A9B}
AppName=Glimpse
AppVersion={#AppVersion}
AppPublisher=TWH
AppPublisherURL=https://github.com/TWH393/Glimpse
AppSupportURL=https://github.com/TWH393/Glimpse/issues
DefaultDirName={localappdata}\Programs\Glimpse
DefaultGroupName=Glimpse
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#AssetDir}
OutputBaseFilename=Glimpse-{#AppVersion}-win-x64-offline-setup
SetupIconFile=..\WpfApp1\Assets\glimpse.ico
UninstallDisplayIcon={app}\Glimpse.exe
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
LZMANumBlockThreads=2
DiskSpanning=yes
DiskSliceSize=1500000000
SlicesPerDisk=1
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Glimpse {#AppVersion}
VersionInfoVersion=0.1.0.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,__pycache__\*,*.pyc,*.pyo"

[Icons]
Name: "{group}\Glimpse"; Filename: "{app}\Glimpse.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Glimpse"; Filename: "{app}\Glimpse.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Uninstall Glimpse"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Glimpse"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Glimpse.exe"; WorkingDir: "{app}"; Description: "Launch Glimpse"; Flags: nowait postinstall skipifsilent
