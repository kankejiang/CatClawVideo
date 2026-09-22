# 用户端运行环境要求

> 结论：**两个平台都不需要用户安装任何开发环境**（.NET / Windows App SDK / QEMU / Java 全部随包）。
> 唯一未随包的第三方依赖是 **VC++ 运行库**，见 §3 —— 这是待修项。
>
> 核查日期：2026-09-22　方法：读 csproj / 出包脚本 + **实测构建产物**（不是转述发行说明）。

---

## 1. Windows

| 依赖 | 用户是否需要装 | 依据 |
|---|---|---|
| .NET 运行时 | **不需要** | 出包脚本 `build-win-release.ps1:146` 用 `-p:SelfContained=true` |
| Windows App SDK 运行时 | **不需要** | `CatClawVideo.Maui.csproj:28` `WindowsAppSDKSelfContained=true`；产物实测含 `CoreMessagingXP.dll` / `MRM.dll` / `Microsoft.UI.*.dll` |
| QEMU（磁力播放引擎） | **不需要** | `ThunderRuntime/`（QEMU + `pkg_kernel` + `pkg_initrd.gz`，约 **144MB**）随包 |
| **Java（jar 类爬虫源）** | **不需要**（2026-09-22 起） | `JavaBridge/jre/`（jlink 自 Microsoft OpenJDK 21，约 **62MB**）随包；`JavaSpiderRuntime.FindJavaExe()` 优先用它 |
| VC++ 2015-2022 运行库 | **⚠ 需要，但当前未随包** | 见 §3 |

**系统门槛**：**Windows 10 1809（`10.0.17763`）及以上 · x64**
（`TargetPlatformMinVersion=10.0.17763.0`；Inno 脚本 `ArchitecturesAllowed=x64compatible`）

**安装形态**：`catclaw.video-x.y.z-Setup.exe`（Inno Setup，`PrivilegesRequired=admin`，装完可直接运行）

---

## 2. Android

| 项 | 要求 |
|---|---|
| 系统版本 | **Android 12（API 31）及以上**（`SupportedOSPlatformVersion=31.0`） |
| ABI | **arm64-v8a** 签名包 |
| 需要安装的环境 | **无** —— jar/dex 爬虫走系统自带的 ART（`DexClassLoader`），JS 爬虫用 Jint 纯托管 |
| 磁力播放 | Android 端不支持（迅雷引擎桥仅 Windows 有） |

---

## 3. ⚠ 待修：VC++ 运行库未随包

用 PE 导入表逐个扫产物里的原生 DLL，结果：

```
依赖 VC++ 运行库的随包文件（共 8 个）：
  FFmpegInteropX.dll  → CONCRT140 · MSVCP140 · VCRUNTIME140 · VCRUNTIME140_1
  avcodec-62 / avdevice-62 / avformat-62 / avutil-60 / swresample-6 / swscale-9 → VCRUNTIME140
  avfilter-11         → MSVCP140 · VCRUNTIME140 · VCRUNTIME140_1

app-local 部署：VCRUNTIME140 / VCRUNTIME140_1 / MSVCP140 / MSVCP140_1 / MSVCP140_2 / CONCRT140 → 全部缺
Inno 脚本：没有 vcredist / dotnet / WindowsAppSDK 任何依赖检查或捆绑
```

**影响**：干净 Windows 上加载 FFmpeg 会失败 ⇒ **视频播放不通**。
开发机因 System32 里已装了 VC++ 运行库（v14.50.35719.00）而**不会暴露**。

★ 对照：Windows App SDK 的原生 DLL **只依赖 `api-ms-win-crt-*`（Windows 自带的 UCRT）**，
不需要 VC++ 运行库 —— **只有 FFmpeg 系与 FFmpegInteropX 需要**。

**修复选项**：
- **A（用户零操作）**：把这 6 个 CRT DLL 做 app-local 放进程序目录（源：VC Redist 的 `Microsoft.VC143.CRT\x64\`）。
- **B**：Inno 里加 `[Files]+[Run]`，用 `[Code]` 判注册表键，缺则静默装官方 `vc_redist.x64.exe`。

---

## 4. 怎么自检"是否开箱即用"

```powershell
# 1) 产物里这几样必须都在
$o = "CatClawVideo.Maui\bin\win-release\publish"
"JavaBridge\jre\bin\java.exe"   # Java 运行时（jar 爬虫源）
"ThunderRuntime\qemu-system-aarch64.exe"  # 磁力播放引擎
Test-Path "$o\coreclr.dll"      # 非空 = self-contained（不依赖用户装 .NET）
```

应用内：**设置页 → 诊断日志**，看这两行即可判定 spider 运行时就绪情况：

```
[源] 站点合计=N 可播=M jar桥=True js=True 桥目录=…
```

> 若 `jar桥=False`：说明 `JavaBridge/bridge.jar` 或 `JavaBridge/jre/bin/java.exe` 没随包/被删 ——
> 正常情况下**不该出现**（Java 已随包）。此时对应站点会显示「爬虫源 · 需 spider 运行时」。
