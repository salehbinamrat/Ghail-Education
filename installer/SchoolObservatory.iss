#define AppName "المرصد المدرسي"
#define AppVersion "0.1.2"
#ifndef AppArch
  #define AppArch "x64"
#endif
#ifndef PublishDir
  #error "PublishDir must be passed to ISCC with /DPublishDir=<absolute-path>"
#endif

[Setup]
AppId=SchoolObservatory-{#AppArch}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=School Observatory
DefaultDirName={localappdata}\Programs\SchoolObservatory-{#AppArch}
DefaultGroupName={#AppName} ({#AppArch})
OutputDir=..\artifacts\installers
OutputBaseFilename=SchoolObservatory-{#AppArch}-Installer
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\SchoolObservatory.exe
WizardStyle=modern
SetupIconFile=..\assets\SchoolObservatory.ico
#if AppArch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "اختصارات التطبيق:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\المرصد المدرسي"; Filename: "{app}\SchoolObservatory.exe"
Name: "{autodesktop}\المرصد المدرسي ({#AppArch})"; Filename: "{app}\SchoolObservatory.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SchoolObservatory.exe"; Description: "تشغيل المرصد المدرسي"; Flags: nowait postinstall skipifsilent
