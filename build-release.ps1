# 猫爪影视 Release APK 构建脚本（真机 arm64 版）
# 用法: .\build-release.ps1
# 输出: CatClawVideo.Maui\bin\Release\net11.0-android\publish\com.catclaw.video-Signed.apk
#
# 说明: 固定输出 arm64 单包，供 ARM64 真机安装。传 -p:ReleaseAbi=arm64，
#       由 csproj 的 SelectReleaseAbi Target 覆盖 RuntimeIdentifiers，
#       AndroidSupportedAbis 自动派生为 arm64-v8a，避免双 ABI 重复打包原生库。
#
# 说明: 构建前强制清理 bin/obj（全量重建），规避增量构建下 typemap/ACW 不一致
#       导致的 JNI UnsatisfiedLinkError 启动闪退。
#
# 说明: 脚本结尾会等待按键再关闭窗口，便于双击运行时查看构建结果/报错。

$ErrorActionPreference = "Stop"

# === 暂停并退出（成功倒计时 60 秒自动退出；失败时等待 Enter） ===
function Pause-And-Exit {
    param([int]$Code = 0)
    Write-Host ""
    if ($Code -eq 0) {
        Write-Host "构建流程结束。" -ForegroundColor Green
    } else {
        Write-Host "构建流程异常终止（退出码 $Code）。" -ForegroundColor Red
        Write-Host "按 Enter 键关闭窗口..." -ForegroundColor Gray
        Read-Host | Out-Null
        exit $Code
    }

    $countdown = 60
    Write-Host "构建完毕，按 Enter 立即退出，或 $countdown 秒后自动退出..." -ForegroundColor Gray
    try {
        for ($i = $countdown; $i -gt 0; $i--) {
            if ($Host.UI.RawUI.KeyAvailable) {
                $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
                break
            }
            Start-Sleep -Seconds 1
        }
    } catch {
        Start-Sleep -Seconds $countdown
    }
    exit $Code
}

# === 配置 ===
$ProjectPath = "CatClawVideo.Maui\CatClawVideo.Maui.csproj"
$TargetFramework = "net11.0-android"
$Config = "Release"
$AppId = "com.catclaw.video"

# 签名信息（与 SIGNING.md 一致）
$KeyStorePath = "catclaw.keystore"
$KeyAlias = "catclaw"
$KeyPass = "catclaw123"
$StorePass = "catclaw123"

# SDK 路径
$AndroidSdk = "C:\Users\$env:USERNAME\AppData\Local\Android\Sdk"
$JavaSdk = "C:\Program Files\Microsoft\jdk-21.0.11.10-hotspot"
$DotNetPath = "C:\Program Files\dotnet\dotnet.exe"

Write-Host "=== 猫爪影视 Release APK 构建 ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $DotNetPath)) { Write-Error "未找到 dotnet.exe: $DotNetPath"; Pause-And-Exit 1 }
if (-not (Test-Path $AndroidSdk)) { Write-Error "未找到 Android SDK: $AndroidSdk"; Pause-And-Exit 1 }
if (-not (Test-Path $JavaSdk))    { Write-Error "未找到 Java SDK: $JavaSdk"; Pause-And-Exit 1 }
if (-not (Test-Path $KeyStorePath)) { Write-Error "未找到签名文件: $KeyStorePath"; Pause-And-Exit 1 }

Write-Host "[1/4] 清理旧构建（全量重建）..." -ForegroundColor Yellow
Remove-Item -Path "CatClawVideo.Maui\bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "CatClawVideo.Maui\obj" -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path "CatClawVideo.Core\bin","CatClawVideo.Core\obj","CatClawVideo.Data\bin","CatClawVideo.Data\obj" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  清理完成" -ForegroundColor Green

Write-Host ""
Write-Host "[2/4] 构建 Release APK（签名，arm64）..." -ForegroundColor Yellow

$OutputDir = "CatClawVideo.Maui\bin\$Config\$TargetFramework"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

& $DotNetPath publish $ProjectPath `
    -c $Config `
    -f $TargetFramework `
    -p:ReleaseAbi=arm64 `
    -p:Aapt2DaemonMaxInstanceCount=0 `
    -m:1 `
    -p:AndroidSdkDirectory="$AndroidSdk" `
    -p:JavaSdkDirectory="$JavaSdk" `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore="$PWD\$KeyStorePath" `
    -p:AndroidSigningKeyAlias="$KeyAlias" `
    -p:AndroidSigningKeyPass="$KeyPass" `
    -p:AndroidSigningStorePass="$StorePass"

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "构建失败！"
    Pause-And-Exit $LASTEXITCODE
}

$signedApk = "$OutputDir\publish\$AppId-Signed.apk"
if (-not (Test-Path $signedApk)) {
    $found = Get-ChildItem -Path $OutputDir -Filter "$AppId-Signed.apk" -Recurse | Select-Object -First 1
    if (-not $found) { Write-Error "构建成功但未找到签名 APK"; Pause-And-Exit 1 }
    $signedApk = $found.FullName
}

$dest = Join-Path $OutputDir "$AppId-Signed.apk"
Copy-Item $signedApk $dest -Force
$builtApk = Get-Item $dest
$stopwatch.Stop()

Write-Host ""
Write-Host "[3/4] 构建成功！" -ForegroundColor Green
Write-Host "  用时: $($stopwatch.Elapsed.ToString('mm\:ss'))"
Write-Host ""
Write-Host "[4/4] 构建结果:" -ForegroundColor Cyan
Write-Host "  文件: $($builtApk.FullName)"
Write-Host "  大小: $([math]::Round($builtApk.Length / 1MB, 2)) MB"
Write-Host "  时间: $($builtApk.LastWriteTime)"
Write-Host ""
Write-Host "=== 构建完成 ===" -ForegroundColor Green

Pause-And-Exit 0
