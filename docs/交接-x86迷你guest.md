# 工作交接文档：x86 Mini Guest（Android 13 转译运行时）

> 更新日期：2026-09-27
> 状态：阶段 1 完成（可用）、阶段 2 转译攻坚进行中（卡在最后一环）
> 交接范围：x86 mini guest 全部工作 + 关联的性能优化/瘦身背景

---

## 0. 一页速览（TL;DR）

**这是什么**：猫爪视频的第二个爬虫运行时——用 QEMU + WHPX 跑 x86_64 的 Android 13，
替代现有 aarch64 TCG 模拟 guest，把聚合网盘源的解析从 71~90s 降到秒级。
同时它保留了独家能力：支持 TVBox Guard 壳源（市面唯一）。

**现在就能用的**：
```powershell
# Windows 宿主（开发机）
$env:CATCLAW_X86_GUEST = '1'     # 不设 = aarch64 现网行为，零变化
# 启动应用 → 日志 %APPDATA%\CatClawVideo.debug\home-debug.log 看「桥就绪」
```

**当前卡点一句话**：ndk_translation 的 ARM 转译链已打通到「主线程 JNI 全部正常」，
但经 DexClassLoader 加载的壳类发起的 JNI 调用查不到已注册的 native 方法
（嫌疑：转译线程的 JNI 查找域 / classloader namespace）——对照实验已埋好，详见 §6。

**接手第一步**：读本文档 §4（操作手册）→ 跑通 §4.2 的 108 侧测试 → 看 §6 的卡点。

---

## 1. 项目背景

### 1.1 为什么做

- 猫爪视频是**市面上唯一支持 TVBox Guard 壳源**的 PC 端（壳的 OLLVM 混淆 arm64 so
  需要「能执行 ARM native 的真 ART」），这是我们独有的护城河。
- 聚合网盘源（玩偶/云搜等）的 `detailContent` 在 aarch64 TCG 模拟的 ART guest 里
  要 **71~90 秒**；同一个 jar 在应用宝电脑版（x86 原生 + ARM 转译）**不到 10 秒**。
- 根因：QEMU TCG 指令级模拟比原生慢 10~20 倍（几十次 TLS 握手全是模拟 CPU 计算）。
- 用户决策链：QEMU + x86 Android + WHPX 硬件虚拟化；Android 9 太老（CA 证书 2021 停更）
  **至少要 12+**；模拟器产品化以后再说。

### 1.2 目标与验收

| 指标 | 现状（aarch64 TCG） | 目标（x86 + WHPX） |
|------|--------------------|--------------------|
| 玩偶 detailContent | 71.6s | 秒级 |
| Android 版本 | 9（CA 证书停更） | 13 |
| Guard 源 | 原生可用 | libndk 转译（实测中） |
| 镜像体积 | 226MB | 与之相当（13 的代价换 12+ 的合规性） |

---

## 2. 系统架构

```
Windows 宿主
├─ CatClawVideo.Maui（C# 宿主）
│   └─ JavaSpiderRuntime ──CATCLAW_X86_GUEST=1──▶ QemuArtGuest(GuestArch.X86_64)
│                                                    │
├─ QemuGuest\（部署目录，原 ThunderRuntime）
│   ├─ qemu-system-x86_64.exe + 40 个 MSYS2 DLL + share\（含 28 个 BIOS bin）
│   └─ x86guest\
│       ├─ vmlinuz-6.1.0-50-amd64（Debian 内核，含 binder_linux.ko）
│       └─ art_initrd_x64.gz（260MB：busybox 基座 + Android 13 子集 + 桥）
│
▼ QEMU（-M q35 -accel whpx 优先，tcg 兜底）
├─ guest 内核 6.1.0-50-amd64（insmod virtio 系 + binder_linux）
├─ /init（init_x86.sh）：网络 → ld.config → fakelogd → proppreload → artlaunch
│   ├─ proppreload_x64.so：Android 属性垫片（161 条）+ boot natives 注册（18 个）
│   ├─ artlaunch.x64：JNI_CreateJavaVM 直调 + 手工 LoadNativeBridge/Initialize
│   └─ gb.dex：桥（bridge.Server）监听 :18600 ←TCP 行协议（与 aarch64 guest 完全一致）
└─ Android 13 子集（Waydroid LOS20 抽取 + ndk_translation 转译层）
```

两条链路协议完全一致，宿主代码架构无关（`GuestArch` 参数化）：
- aarch64 guest：`-M virt -accel tcg` + `art_initrd.gz`（Android 9）——现网
- x86 guest：`-M q35 -accel whpx` + `x86guest\art_initrd_x64.gz`（Android 13）——实验

---

## 3. 环境与资产清单（接手先看这里）

### 3.1 机器与账号

| 位置 | 路径/地址 | 用途 |
|------|----------|------|
| Windows 开发机 | `D:\Code\CatClawVideo` | 主仓库 |
| **Linux 联调机** | `root@10.0.0.108`（ssh 免密） | **Proxmox VE 宿主，20 核/31GB，KVM 原生加速**——联调迭代全在这台做 |
| 108 工作区 | `/root/x86guest/` | 全部联调素材与脚本（见 §3.3） |
| Windows 工作数据 | `D:\Code\.shot\` | 镜像/解包产物/脚本副本 |
| TVBox 源码 | `D:\Code\sourceCode-ccc25f6\` | TVBox_debug-java64.apk（桥的 dex 来源） |
| NDK | `C:\Users\lvjin\AppData\Local\Android\Sdk\ndk\27.0.12077973` | guest 原生件编译（clang --target=x86_64-linux-android28） |
| 7-Zip | `C:\Program Files\7-Zip\7z.exe` | **24.08+，支持 ext4 与 Android LP super**（解镜像的神器） |
| 日志 | `%APPDATA%\CatClawVideo.debug\home-debug.log` | 宿主+guest 全链路日志 |

### 3.2 仓库关键位置

| 路径 | 内容 |
|------|------|
| `CatClawVideo.Core/Services/QemuGuest/` | 宿主编排（QemuHostRuntime/QemuArtGuest/QemuThunderEngine 等 11 文件）——2026-09-27 由 QemuThunder 更名 |
| `CatClawVideo.Core/Providers/JavaSpiderRuntime.cs` | 桥的宿主侧：`_guestArchOverride`/`_guestKernelFile` 等开关字段；`CATCLAW_X86_GUEST=1` 触发 |
| `CatClawVideo.Maui/QemuGuest/` | 部署目录（原 ThunderRuntime）——csproj `QemuGuest\**` 通配拷贝 |
| `JavaBridge/qemu-src/art/` | artlaunch.c / proppreload.c / fakelogd.c / gen_props.py / props_gen_x64.h / gb.dex / 双架构编译产物 |
| `JavaBridge/qemu-src/tools/x86guest/` | 组装/测试/诊断脚本（见 §4） |
| `JavaBridge/qemu-src/tools/mk_art_initrd.py` | aarch64 initrd 重打管线 |
| `docs/x86-mini-guest.md` | 技术日志（攻坚过程全记录，与本文档互补） |

### 3.3 108 主机 `/root/x86guest/` 资产

| 文件 | 说明 |
|------|------|
| `system.img`（1.8G） | Waydroid LOS20 Android 13 x86_64 完整 rootfs（ext4，loop 可挂） |
| `sysimg-ex/x86_64/system.img`（4.1G） | **官方模拟器镜像**（google_apis/x86_64-33_r12，GPT+LP+ext4）——对照基准，已解出到 `official/` |
| `official/` | 官方镜像解出的 /system 内容（7-Zip 直接解）——**ndk 配置的黄金参考** |
| `SuperOut/system.img` | 官方 super 里的 system（LP 解出） |
| `linux-data/boot/vmlinuz-6.1.0-50-amd64` | Debian 6.1.176 内核（binder 模块树在 `lib/modules/`） |
| `qemu-data/usr/bin/qemu-aarch64-static` | 用户态模拟器（备用方案，未启用） |
| `busybox`（x86 静态） | initrd 基座 |
| `artlaunch.x64 / proppreload_x64.so / fakelogd.x64` | guest 原生件（Windows 编译后 scp 推来） |
| `mk_x86_initrd.sh` | **initrd 组装脚本（核心）**——rootfs 组装+config 生成+cpio 打包 |
| `init_x86.sh` | guest 内 /init（模块→网络→binfmt→fakelogd→proppreload→artlaunch） |
| `ld_config_x86.txt` | x86 linker 配置参考 |
| `run_x86_test.sh` / `mi_test.sh` | 启动测试（后者带桥就绪计时） |
| `diag.sh` | 装配诊断（模块/网络/config/artlaunch 各阶段） |
| `bench.py` | 桥基准：ping → load(tvbox.apk 或 Guard jar) 计时 |
| `dec2.sh` | logd 报文结构化解码（fakelogd hex 模式配套） |
| `art_initrd_x64.gz`（278M） | **组裝产物**——即部署目录的 x86guest/art_initrd_x64.gz |

---

## 4. 操作手册

### 4.1 guest 原生件编译（Windows）

```powershell
$NDK = "C:\Users\lvjin\AppData\Local\Android\Sdk\ndk\27.0.12077973\toolchains\llvm\prebuilt\windows-x86_64"
$art = "D:\Code\CatClawVideo\JavaBridge\qemu-src\art"
# aarch64（现网）
& "$NDK\bin\clang.exe" --target=aarch64-linux-android28 -O2 -fPIC -shared proppreload.c dnsshim.c -o proppreload.so
& "$NDK\bin\clang.exe" --target=aarch64-linux-android28 -O2 '-Wl,--export-dynamic' artlaunch.c -o artlaunch -ldl
# x86_64（mini guest）——proppreload 必须带 -DX86_GUEST（选 x64 属性表）
& "$NDK\bin\clang.exe" --target=x86_64-linux-android28 -O2 -fPIC -shared -DX86_GUEST proppreload.c dnsshim.c -o proppreload_x64.so
& "$NDK\bin\clang.exe" --target=x86_64-linux-android28 -O2 '-Wl,--export-dynamic' artlaunch.c -o artlaunch.x64 -ldl
# 属性表生成（art 目录下执行；aarch64 版默认，勿动现网）
python gen_props.py x86_64     # → props_gen_x64.h（镜像源 x86sys/ = Waydroid build.prop）
```

⚠ `&&` 在 PowerShell→python heredoc 管道中会被损坏成 `;`——涉及 C 代码生成时用
python `chr(38)*2` 拼接或直接写文件。⚠ 编译产物不要带 `-lpthread`（bionic 的 pthread 在 libc 内）。

### 4.2 108 侧组装+启动+基准（一条龙）

```bash
ssh root@10.0.0.108
cd /root/x86guest
bash mk_x86_initrd.sh        # 组装（产物 art_initrd_x64.gz ~278M）
bash mi_test.sh              # 起 KVM qemu + 桥就绪计时（★ 桥就绪 9s）
python3 bench.py             # 基准：ping → load（Guard jar 转译实测）
bash diag.sh                 # 各阶段诊断
```

### 4.3 Windows 宿主接通

```powershell
# 部署（构建输出目录也需同步——PreserveNewest 只在 dotnet build 时拷）
dotnet build CatClawVideo.Maui -f net11.0-windows10.0.26100.0
$env:CATCLAW_X86_GUEST = '1'
Start-Process "...\bin\Debug\net11.0-windows10.0.26100.0\win-x64\CatClawVideo.Maui.exe"
# 日志关键行：[qemu] console=ttyS0（x86 分支生效）→ SeaBIOS → [init] → artlaunch → 桥已连上
```

### 4.4 常见故障速查

| 症状 | 原因 | 处置 |
|------|------|------|
| `could not load PC BIOS 'bios-256k.bin'` | share\ 缺 BIOS（x86 必需，aarch64 不需要） | 拷 28 个 bin 到 QemuGuest\share\（源+输出目录都要） |
| 起播报「Cannot access a disposed object」 | 桥流被 finally 误杀 | 已修（artlaunch 探针 c=null），复现则查 ConnectAsync 所有权 |
| 首页卡「正在加载影片…」 | guest 内缺库 panic | 看 home-debug.log 的 `[logd]` 行找缺的库名 |
| 构建输出报文件锁 | 应用正在运行 | 关掉应用再 build |

---

## 5. 已解决的坑清单（勿重复踩）

> 每条都是实测踩出来的，详细过程见 `docs/x86-mini-guest.md` 技术日志。

| # | 坑 | 根因 | 解法 |
|---|-----|------|------|
| 1 | boot 镜像加载即 abort | 内嵌 APEX 版本字段与运行时校验不匹配 | 排除 `boot*.art/oat/vdex/bprof`，走 imageless+JIT |
| 2 | dex2oat 现场生成撞 namespace 死结 | 生成要求与极简 rootfs 无法满足 | 删 dex2oat64 + JVM 选项 `-Xnoimage-dex2oat -Xnodex2oat` |
| 3 | `RegisterNatives` 找不到 `com/android/icu/text/*` 直接 abort | Android 12+ 把 icu4j 从 art apex 移到 **i18n apex** | core-icu4j.jar 拷进 /system/javalib 并入 BCP |
| 4 | apex namespace 反复 abort | BCP 里 /apex 路径需要 apex namespace | core jar 拷到 /system/javalib 绕行 + BCP 环境变量化 |
| 5 | ICU/时区数据找不到 | 无 init 设置 ANDROID_I18N_ROOT 等 | 环境变量 + tzdata apex 拷贝 |
| 6 | `no namespace called com_android_art` | 自定义 namespace 未声明 | ld.config 追加 `additional.namespaces` 规则 |
| 7 | 黑名单裁剪 → Kernel panic | libandroid.so 静态链接 libpdfium + 公共库预加载 | **裁剪只允许闭包法**（slim_sys28.py） |
| 8 | qemu 启动报缺 DLL | 静态 import 链（libavif 等 6 个） | 删 DLL 必须全量互扫+实际启动验证 |
| 9 | `could not load PC BIOS` | x86 qemu 需要 PC BIOS（aarch64 直启不需要） | share\ 补 28 个 bin |
| 10 | `/init` 执行 EACCES(-13) | Windows scp 丢执行位 | mk 脚本统一 chmod |
| 11 | binfmt 注册 nonexistent directory | procfs 不支持 mkdir | 挂载点用 rootfs `/binfmt_misc` |
| 12 | ndk initialize 静默失败 | 属性组不全 | 对照 mk 文件补 vendor.exec64/ndk version/flags |
| 13 | arm64 wrapper `CANNOT LINK ... EM_X86_64` | arm64 linker 读到 x86 的 ld.config | **/system/etc/ld.config.arm64.txt**（按 ISA 分离，官方机制） |
| 14 | config 解析失败 `section "system" not found` | 缺 `[system]` 段头 | bionic config 语法要求段头 |
| 15 | fakelogd 只输出 tag 丢 msg | 只做可打印串抽取 | 重写为 logdw 协议解析（prio@11/tag/msg），hex 诊断模式 LOGD_HEX=1 |
| 16 | loop 挂载残留 → cp 全挂 | 上次会话未 umount | mount 前 `umount ... \|\| true` |

---

## 6. 当前卡点详情（ndk 转译的最后一环）

### 6.1 已打通的部分（不要重做）

- 属性层：`ro.dalvik.vm.native.bridge=libndk_translation.so` 等 7 项全部可达（artlaunch 探针实锤）
- 转译器主库：`dlopen libndk_translation = OK`
- `LoadNativeBridge = 1`（手工调用；libart 自己只 Load 不 Init）
- `PreInitializeNativeBridge = 1`（binfmt+runner 链活，arm64 wrapper 真被 runner 转译执行）
- **`Initialized NDK translation (aarch64), version 0.2.3`**（日志实锤，initialize 已通过）
- cpuinfo 覆盖文件（bind-mount 依赖）
- **主线程自测双过**：`SystemProperties.get` 与 `native_get` 直调都返回正确值

### 6.2 卡住的现象

经 `DexClassLoader` 加载的壳类（DexNative.<clinit> → Build.CPU_ABI）发起的
`SystemProperties.native_get(String,String)` 调用报 `No implementation found`，
但同样的调用在主线程（自测）成功。注册是类级的，理论上线程无关——
**嫌疑**：①转译执行线程的 JNI 查找域（ndk initialize 对非主线程的影响）；
②app dex 类解析 SystemProperties 时走了非 boot 视角（classloader namespace）。

### 6.3 已排除的方案（勿重试）

| 尝试 | 结果 |
|------|------|
| `LD_LIBRARY_PATH=/system/lib64/arm64` | ndk loader 不读 |
| ld.config.txt 插 arm64 路径 | x86 侧解析即崩（linker DEBUG 失败） |
| 跳过 PreInitialize 直调 Initialize | 静默失败（返回 0） |
| 文件层对照（库/config） | 已完全对齐，非文件问题 |

### 6.4 下一步（按优先级）

1. **排障线程域对照实验**（已埋在 artlaunch：pthread + AttachCurrentThread + 主线程/
   新线程双对照）——实验块入口 printf 分步定位为什么输出没上串口。新线程失败坐实 →
   查 ndk 主库 strings 里的线程相关开关；成功 → 转向 2。
2. **桥侧取证**：改 Server.java 在 load 流程打印壳类的实际 ClassLoader 与 SystemProperties
   来源（重编 gb.dex 走 JavaBridge/build.cmd 链路）。
3. **开源源码**：找 google/ndk_translation 的 mirror（GitHub 搜索未果，可试
   chromium.googlesource.com 的 ARC++ 分支或 Bliss-x86 的 vendor 仓库 issue 区）。
4. **兜底**：双 guest 路由（C# 按源分流——非 Guard 走 x86 原生、Guard 走 aarch64），
   工程量小、可先把非 Guard 源的原生速度落地。

---

## 7. 未完成任务

- [ ] ndk 转译 classloader/线程域问题（见 §6）
- [ ] 双 guest 路由的 C# 实现（按源分流）
- [ ] WHPX 不可用用户的一键启用引导（设置页，DISM VirtualMachinePlatform）
- [ ] 发行打包（build-win-release.ps1 带 x86 组件，预计 +300MB）
- [ ] Guard 转译打通后的真机验收：玩偶 detailContent 71.6s → 秒级对照
- [ ] 镜像裁剪第二轮：framework boot 分件与 framework-res.apk（需动 boot classpath，风险高一档）

---

## 8. 提交历史索引（按主题）

| 主题 | 提交 |
|------|------|
| 性能优化（背景期） | a12d16f 桥预热+互斥 · 00c26bc 无限翻页 · 98e591f 超时报错 · 45e5314 搜索提速 · cd2fa03 跨源预取 · 5a05c19 探桥生死+detail缓存 · 4c53e54 vCPU 4 |
| 瘦身实验（教训库） | 8966f97 黑名单裁剪 · a0d4e3f libavif 误删恢复 · 472c5bc 闭包法 |
| x86 骨架 | b83a565 qemu x86 引入 · c631ebd 联调工具链 · 04b0297 chroot 调试 · ce782cb 架构参数化 · 5cca317 续跑脚本 · 2e0ce15 宿主开关 · 8f303d4 **里程碑 9s/19×** |
| 命名统一 | 7eb725e QemuThunder→QemuGuest · c5d11e8 gitignore · 2a787ef x86 部署 |
| ndk 攻坚 | 57c8356 BIOS 补齐 · dd40c07 属性架构化 · d733c57 fakelogd 协议解析+死因 · 6f7f52f arm64 config · 28f2408 官方镜像对照 |

---

## 9. 相关文档

- `docs/x86-mini-guest.md` —— 技术日志（每一步的实测数据与推理，与本文档互补）
- `docs/qemu-tcg-tuning.md` —— aarch64 TCG 调参（vCPU 峰值区间等）
- `docs/user-runtime-requirements.md` —— 用户运行时要求（虚拟化平台等）
- `JavaBridge/qemu-src/README.md` —— qemu-src 子工程说明

## 10. 交接注意事项

1. **`.shot/` 与 108 的素材是易失的**——108 若重装，Waydroid 镜像可重新下载
   （OTA 索引 `ota.waydro.id/system/lineage/waydroid_x86_64/VANILLA.json` → SourceForge 直链），
   sys28 的 aarch64 dump 在 `D:\Code\.shot\sys28`（只读原副本）。
2. **`&&` 转义坑**：PowerShell → ssh → bash 的多层管道会损坏 `&&`（变 `;`）——
   复杂命令一律写脚本文件 scp 过去执行，勿内联。
3. **订阅环境是 Guard 源为主**（46 个 jar 全是 `*Guard` 类）——x86 guest 的转译没打通前，
   x86 模式下这些源不可用（宿主自动回退并发探测到 MacCMS 源）——**不要在没看 §6 的情况下
   建议用户切 x86**。
4. **数据目录区分**：正式版 `%APPDATA%\CatClawVideo\`、Debug 版 `%APPDATA%\CatClawVideo.debug\`。
5. 改代码**必须 git commit**（用户硬性要求），提交信息用中文按
   `type(scope): 现象——根因` 风格，正文逐项写动机。
