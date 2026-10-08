[Setup]
AppName=BtChat
AppVersion=1.0.0
AppPublisher=BtChat
DefaultDirName={autopf}\BtChat
DefaultGroupName=BtChat
OutputDir=Output
OutputBaseFilename=BtChat-Setup-1.0.0
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\BtChat.exe
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; مسیر دقیق پوشه publish نسبت به محل قرارگیری فایل iss
Source: "BtChat\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BtChat"; Filename: "{app}\BtChat.exe"
Name: "{autodesktop}\BtChat"; Filename: "{app}\BtChat.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BtChat.exe"; Description: "Launch BtChat"; Flags: nowait postinstall skipifsilent