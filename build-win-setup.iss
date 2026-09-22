; 猫爪影视 Windows 安装程序脚本（Inno Setup 6.3+/7）
; 用法: "C:\Users\lvjin\InnoSetup\ISCC.exe" build-win-setup.iss
;       或  .\build-win-release.ps1  （一键：publish + 编译安装包）
; 打包源: CatClawVideo.Maui\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish（self-contained 绿色目录发布）
; 依赖: installer\vc_redist.x64.exe（微软官方的 VC++ 2015-2022 x64 可再发行组件，约 24MB）——
;       随包的文件里有 8 个原生 DLL 依赖它（FFmpeg 系 + FFmpegInteropX），
;       已在系统里装过则跳过（见下方 [Code] VCRedistPresent）。
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
; VC++ 2015-2022 运行库（x64）。放到 {tmp} 并在装完后删除，不往用户机器上留垃圾文件。
; ⚠ 必须是**完整包**（约 24MB），不能是 VS Package Cache 里那种 600KB 的下载器桩（离线装不上）。
Source: "installer\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Run]
; 运行库先装（缺则装、有则跳过），再让用户选是否启动应用。
; 不带 postinstall ⇒ 在安装过程中静默执行（用户无需勾选）；/norestart 交回安装程序处理重启。
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; \
  StatusMsg: "正在安装运行库（Visual C++ 2015-2022 x64）…"; Flags: runhidden; Check: NeedsVCRedist
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}(&R)"; Flags: nowait postinstall skipifsilent
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// ═══════════════════════════════════════════════════════════════════════
// VC++ 2015-2022 运行库（x64）—— 为什么非要装
//   实测产物的 PE 导入表，随包里 8 个原生 DLL 依赖它：
//     FFmpegInteropX.dll → CONCRT140 / MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
//     avcodec-62 / avdevice-62 / avformat-62 / avutil-60 / swresample-6 / swscale-9 → VCRUNTIME140
//     avfilter-11        → MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
//   而 Windows App SDK 的原生库**不需要**它（只依赖系统自带的 UCRT：api-ms-win-crt-*），
//   所以这一项只决定「能不能播放视频」。
//   缺它时干净 Windows 上加载 FFmpeg 会失败 ⇒ 播放不通；开发机因装过 VC++ 不会暴露。
// ═══════════════════════════════════════════════════════════════════════
// ⚠ Inno 的 Pascal Script 不支持函数内的 `const` 段（编译报 "BEGIN expected"），只能用 var。
function RegDwordOr0(const SubKey: String; const Name: String): Cardinal;
var
  V: Cardinal;
begin
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE, SubKey, Name, V) then
    Result := V
  else
    Result := 0;
end;

// 用「注册表 Installed=1 且 Minor >= 29」判定，而不是去测 VCRUNTIME140_1.dll 是否存在：
//   · Minor >= 29 对应 VS2019（14.29）—— 从那一版起才有 VCRUNTIME140_1.dll，
//     能把「只装过 VS2015（14.0）」的机器也归为「需要装」；
//   · 不碰 %System32% 路径是因为本安装包在 32 位进程下测 System32 会被 WOW64 重定向到 SysWOW64，
//     要么用 {sysnative}（旧版 Inno 没有），要么就绕开 —— 注册表判定没有这个坑。
// 两个视图都查：64 位进程看前一个键，万一被重定向还有 WOW6432Node 兜底。
function VCRedistPresent: Boolean;
var
  K64, K32: String;
begin
  K64 := 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  K32 := 'SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  Result :=
    ((RegDwordOr0(K64, 'Installed') = 1) or (RegDwordOr0(K32, 'Installed') = 1))
    and ((RegDwordOr0(K64, 'Minor') >= 29) or (RegDwordOr0(K32, 'Minor') >= 29));
end;

function NeedsVCRedist: Boolean;
begin
  Result := not VCRedistPresent;
end;
