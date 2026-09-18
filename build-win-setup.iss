; 猫爪影视 Windows 安装程序脚本（Inno Setup 6.3+/7）
; 用法: "C:\Users\lvjin\InnoSetup\ISCC.exe" build-win-setup.iss
;       或  .\build-win-release.ps1  （一键：publish + 编译安装包）
; 打包源: CatClawVideo.Maui\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish（self-contained 绿色目录发布）
#define MyAppName "猫爪影视"
#ifndef MyAppVersion
#define MyAppVersion "0.1.1"
#endif
#ifndef MyPublishDir
#define MyPublishDir "CatClawVideo.Maui\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish"
#endif
#define MyAppPublisher "CatClawVideo"
#define MyAppExeName "CatClawVideo.Maui.exe"
#define MyAppId "7A4E1D93-6B25-4F08-9C71-CatClawVideoWin"
[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\CatClawVideo
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=CatClawVideo.Maui\Resources\AppIcon\appicon.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=release\windows
OutputBaseFilename=catclaw.video-{#MyAppVersion}-Setup
LicenseFile=LICENSE
PrivilegesRequired=admin
[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加图标:"; Flags: unchecked
[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}(&R)"; Flags: nowait postinstall skipifsilent
[UninstallDelete]
Type: filesandordirs; Name: "{app}"
