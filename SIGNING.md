# Android APK 签名信息

> 此文件仅供开发参考，开源项目可公开。

| 项目 | 值 |
|------|-----|
| 文件 | `catclaw.keystore`（项目根目录） |
| 格式 | PKCS12，2048-bit RSA（SHA384withRSA 自签名） |
| 别名 (alias) | `catclaw` |
| 密码 | `catclaw123`（storepass = keypass） |
| 主题 | `CN=CatClawVideo, OU=CatClaw, O=CatClaw, C=CN` |
| SHA256 | `D0:52:5D:17:C5:3C:21:FE:26:8D:EB:4C:28:51:11:43:21:0D:1C:DB:3F:03:26:BC:CE:6C:5C:64:A3:F2:68:1A` |
| 有效期 | 2026-09-11 → 2054-01-27（约 27.4 年） |

> ⚠️ 本 keystore 是**猫爪影视专用**，与猫爪音乐的 `catclaw.keystore` 是**两个不同的密钥**
> （两者文件名相同、密码相同，但证书指纹不同）。两者签名不通用，勿互相覆盖。

## Release 编译命令（出签名包）

```bash
dotnet publish CatClawVideo.Maui/CatClawVideo.Maui.csproj -c Release -f net11.0-android \
  -p:ReleaseAbi=arm64 \
  -p:Aapt2DaemonMaxInstanceCount=0 -m:1 \
  -p:AndroidSdkDirectory="C:/Users/Administrator/AppData/Local/Android/Sdk" \
  -p:JavaSdkDirectory="C:/Program Files/Microsoft/jdk-21.0.11.10-hotspot/" \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore="$PWD/catclaw.keystore" \
  -p:AndroidSigningKeyAlias=catclaw \
  -p:AndroidSigningKeyPass=catclaw123 \
  -p:AndroidSigningStorePass=catclaw123
```

或直接运行根目录脚本：`.\build-release.ps1`（自动清 bin/obj 后全量重建）。

csproj 中已配置签名引用 `..\catclaw.keystore`，密码通过 MSBuild 属性传入
（`CatClawKeyPass` / `CatClawStorePass`，已内置兜底 `catclaw123`，命令行可覆盖）。

输出 APK 路径：`CatClawVideo.Maui\bin\Release\net11.0-android\publish\com.catclaw.video-Signed.apk`

## 单 ABI 说明

`-p:ReleaseAbi=arm64` 由 csproj 的 `SelectReleaseAbi` Target 消费，把 `RuntimeIdentifiers`
在项目局部覆盖为单个 RID，`AndroidSupportedAbis` 自动派生为 `arm64-v8a`，
避免双 ABI 包重复打包原生库。不传则维持默认双 RID（`android-arm64;android-x64`）。

## 构建环境

| 项 | 路径 |
|-----|------|
| Android SDK | `C:\Users\Administrator\AppData\Local\Android\Sdk` |
| JDK | `C:\Program Files\Microsoft\jdk-21.0.11.10-hotspot\` |
| dotnet | `C:\Program Files\dotnet\dotnet.exe` |

强制单线程（`-m:1` + `Aapt2DaemonMaxInstanceCount=0`）规避并发构建下的
AAPT2 `XA0111 unsupported AAPT2 version` 问题。
