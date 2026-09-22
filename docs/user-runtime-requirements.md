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
| VC++ 2015-2022 运行库 | **不需要**（安装时自动装，缺则装、有则跳过） | 见 §3 |

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

## 3. VC++ 运行库：已由安装包自动处理（方案 B）

**问题**（2026-09-22 实测 PE 导入表）：随包的 8 个原生 DLL 依赖它 ——

```
FFmpegInteropX.dll  → CONCRT140 · MSVCP140 · VCRUNTIME140 · VCRUNTIME140_1
avcodec-62 / avdevice-62 / avformat-62 / avutil-60 / swresample-6 / swscale-9 → VCRUNTIME140
avfilter-11         → MSVCP140 · VCRUNTIME140 · VCRUNTIME140_1
```

而产物里 **app-local 一个都没带**，安装脚本也没有任何 vcredist 处理 ⇒
干净 Windows 上加载 FFmpeg 失败、**视频播放不通**（开发机因 System32 里已装过而不会暴露）。

★ 对照：Windows App SDK 的原生 DLL **只依赖 `api-ms-win-crt-*`（Windows 自带的 UCRT）**，
不需要 VC++ 运行库 —— **只有 FFmpeg 系与 FFmpegInteropX 需要**。

**实现**（`build-win-setup.iss`）：

| 段 | 作用 |
|---|---|
| `[Files]` | `installer\vc_redist.x64.exe` → `{tmp}`，`deleteafterinstall`（不往用户机器留垃圾） |
| `[Run]` | `vc_redist.x64.exe /install /quiet /norestart`，带 `Check: NeedsVCRedist`（缺才跑） |
| `[Code]` | `VCRedistPresent()` 读注册表判定 |

**判定为「已装」的条件**：`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`
（或 `WOW6432Node` 那份）满足 **`Installed=1` 且 `Minor>=29`**。

- `Minor>=29` 对应 **VS2019（14.29）** —— 从那一版起才有 `VCRUNTIME140_1.dll`，
  能把「只装过 VS2015（14.0）」的机器也正确地归为「需要装」。
- ★ **为什么不直接测 `VCRUNTIME140_1.dll`**：实测本机该文件**只在 `System32`、不在 `SysWOW64`**。
  安装包是 32 位进程，查 `C:\Windows\System32\...` 会被 WOW64 **重定向到 `SysWOW64`** ⇒
  **误判为缺失、每次安装都白跑一遍**。走注册表没有这个坑。

**用到的安装器原件**：`installer\vc_redist.x64.exe`（来自微软官方永久链接
`https://aka.ms/vs/17/release/vc_redist.x64.exe`，25,635,768 B，md5 `486f81fa…`，版本 **14.44.35211.0**）。
⚠ 必须用**完整包**：VS Package Cache 里那种 600KB 的 `VC_redist.x64.exe` 是**下载器桩**（无 MSI/cab 载荷），
离线装不上。

**验证**：
- `ISCC` 编译通过（`Compiling [Code] section` → `Successful compile`），日志确认
  `Compressing: installer\vc_redist.x64.exe (14.44.35211.0)` 已进包
- 在**已装**运行库的本机演算判定：`Installed=0x1`、`Minor=0x32(=50) ≥ 29` ⇒ 判为已装 ⇒ 跳过 ✓

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
