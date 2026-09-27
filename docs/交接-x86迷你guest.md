# 工作交接文档：x86 Mini Guest（Android 13 转译运行时）

> 更新日期：2026-09-27（晚）
> 状态：阶段 1 完成（可用）；阶段 2 转译攻坚：壳解密+真实 dex 加载已通，剩壳初始化后段退出
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

**当前状态一句话**：§6 的「壳类 JNI 查不到 native 方法」卡点已连环攻破——
三个根因（注册时序晚于 ndk initialize、libnativeloader namespace 判定、
LoadNativeBridge 缺 ART callbacks 表）全部修复后，Guard 壳已能解密并加载
真实 dex（「自定义爬虫代码加载成功」日志实锤）。剩最后一环：壳初始化后段
进程静默退出（无 ART dump，疑似转译段 fault 处理链），详见 §6.6。

**接手第一步**：读本文档 §4（操作手册）→ 跑通 §4.2 的 108 侧测试 →
跑 `python3 bench_guard.py` 复现壳加载 → 攻 §6.6 的静默退出。

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
| `JavaBridge/qemu-src/tools/x86guest/` | 组装/测试/诊断脚本（见 §4）；`bench_guard.py` = Guard 壳转译验收（ping→load WoGGGuard→homeContent）；`sym_lookup.py` = libart 符号表解析诊断 |
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
| 17 | 线程实验块无输出静默跳过 | JNI_GetCreatedJavaVMs 误声明两参（真三参），出参 nv 永不写 | 修签名 (vmBuf,1,&nv)；JNI 导出函数签名必须对照头文件 |
| 21 | 一轮会话叠出 3~4 个 qemu 并存 | 重置窗口期竞态：Shutdown 后台跑，置空 _art 前并发调用者拿旧引用 → 旧 QemuHostRuntime 被 StartAsync 复活；随后新调用又 new 新实例 | 四层：Shutdown 先原子摘引 / _bridgeEpoch 代际校验 / QemuArtGuest._disposed 禁复活 / QemuHostRuntime 生命周期锁+按名回收孤儿（2026-09-27，宿主实测收敛为 0~1 个） |
| 22 | 宿主探针连接被 accept 但 pong 永不回 | 信号哨兵每次 SIGSEGV 都 write 到 console，宿主握手期无人读 → 64KB 管道写满 → 信号处理内阻塞，guest 全线程冻住 | 哨兵日志改环境变量开关 CATCLAW_SIGLOG（默认关）；任何高频信号处理内禁用 stdio write |
| 18 | 壳类 Build.CPU_ABI 必死（类 erroneous） | ndk InitNB 触发 Build.<clinit> 时 boot natives 还没注册 | 注册挪到 PreNB/InitNB 之前（artlaunch 主流程重排） |
| 19 | System.load(arm64) 报 EM 架构不符 | libnativeloader 的 namespace bridged 依赖 IsPathSupported，ndk 对一切路径 false | proppreload 接管该回调（/data/catclaw 前缀 → true） |
| 20 | 壳 so 加载 CHECK failed: g_runtime_callbacks | LoadNativeBridge 第二参传 NULL（必须传 ART 的 9 函数 callbacks 表） | 按 libart 静态 vaddr 重建表（artlaunch，锚点 InitializeNativeBridge@0x71ccf0） |

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

### 6.4 下一步（按优先级）——全部执行完毕（2026-09-27 下午）

1. **线程域对照实验** → 实验块静默跳过的原因 + 新线程成功，见 §6.5-①②
2. **桥侧取证** → 未走到：artlaunch 侧审计已足够定位（§6.5-③）
3. **开源源码** → 未需要：108 上反汇编 libart/libnativebridge/ndk 库已拿到全部答案
4. **双 guest 路由兜底** → 暂不需要（壳加载已通）

### 6.5 连环三根因（2026-09-27 下午，全部实锤+修复）

> 排障方法：给 artlaunch 实验块/审计加分步 printf → 修一个、重装 initrd、
> 跑 bench_guard.py（load WoGGGuard）看现象推进。循环 4 轮打穿。

**① 实验块静默 + Build 类永久 erroneous —— 注册时序**
- 现象链：实验块无输出 → 补探针发现 `JNI_GetCreatedJavaVMs` 返回 nv=0 →
  **该函数真实签名是三参 (vmBuf, bufLen, nVMs)，代码误声明两参**，nv 的地址被当
  bufLen=0 传入、出参永不写 → 修签名后 nv=1。
- 新线程 `native_get` 返回 x86_64 **成功** → **线程域假设排除**。
- 但实验块执行期间出现 `Landroid/os/Build; failed initialization` +
  `Failed to register non-native method ... native_get as native`：
  **ndk InitializeNativeBridge 内部触发 Build.<clinit>（读 CPU_ABI 做伪装），
  此时 proppreload 还没注册 boot natives（注册块原本在 InitNB 之后）→
  UnsatisfiedLinkError → Build 类被永久标记 erroneous** → 壳链路任何
  Build.CPU_ABI 访问必死，而主线程自测（不碰 Build）却正常——正是旧卡点现象。
- 修复：`catclaw_register_boot_natives` 挪到 PreNB/InitNB **之前**（CreateVM 后立即）。
  修复后 `Build.CPU_ABI = arm64-v8a`（ndk 伪装层正常工作，壳的 contains("64") 可过）。
- 附带实锤（反射 Modifier 审计）：LOS20 的 `native_get(String)` 单参版 **flags=0xa
  非 native**（Java 实现，走 handle 体系），proppreload 注册它报「non-native」属正常，
  表里该行可删；`native_find_prop(String,[B)` 在 13 里改名 `native_find(String)J`，
  需按 handle 语义重写才能补注册（未做，不阻塞）。

**② System.load 不走 nativebridge —— namespace 判定**
- 现象推进到：壳 `DexNative.<clinit>` 解密出 arm64 so 后 `System.load` 报
  `dlopen failed: ".fty…" is for EM_AARCH64 (183) instead of EM_X86_64 (62)`。
- 排障：审计 `NativeBridgeIsSupported(arm64 so)=1`、`NativeBridgeLoadLibrary(arm64)=OK`
  ——**转译器本体完全可用**；反汇编 libart 发现它只导入
  `NativeBridgeGetTrampoline/Initialized` 等，**加载判定在 libnativeloader**：
  `NativeLoaderNamespace::Create` 对 search_path 逐段调 `NativeBridgeIsPathSupported`
  决定 namespace 是否 bridged；ndk 的该回调对**所有路径**都返回 false
  （实测含官方 `/data/app/…/lib/arm64` 模式）→ namespace 永久 not-bridged → bionic dlopen。
- 修复：proppreload.c（LD_PRELOAD，全局组最前）**接管 `NativeBridgeIsPathSupported`**：
  `/data/catclaw` 前缀 → true（壳的 classloader namespace 只服务壳的 arm64 so，
  全量 bridge 是正确语义），其余 → false（与原实现实测值一致）。
  命名空间 bridged 后加载走 `NativeBridgeLoadLibraryExt` → ndk arm64 linker 转译。
  另：LoadNativeBridge 调用顺序改为先于 InitializeNativeLoader（防 libnativeloader
  缓存「bridge 不可用」）。

**③ 壳 so 加载时 ART abort —— g_runtime_callbacks 为 NULL**
- 现象推进到：`native_bridge.cc:453: CHECK failed: g_runtime_callbacks` → 进程 abort。
- 排障：反汇编 libnativebridge——`LoadNativeBridge(filename, runtime_callbacks)` 把
  **第二参**存全局，`InitializeNativeBridge` 调 ndk 的 initialize 回调时作第一参传入；
  artlaunch 原代码 `LoadNativeBridge("libndk_translation.so", NULL)` → ndk 的
  `g_runtime_callbacks = NULL` → 壳 so 加载时 CHECK 失败。
- 正确值 = libart 静态全局 `art::native_bridge_art_callbacks_`（未导出，`_ZL` 符号）。
  反汇编 `art::LoadNativeBridge` 确认它传该表；dump `.data` 得 9 个函数指针：
  GetMethodShorty / GetNativeMethodCount / GetNativeMethods /
  4 个静态辅助（未导出）/ DexFile closeDexFile+defineClassNative 系。
- 修复：artlaunch 以导出符号 `_ZN3art22InitializeNativeBridgeEP7_JNIEnvPKc`
  （静态 vaddr 0x71ccf0）求运行基址，按静态 vaddr 表 `{0x71cdc0, 0x71d540, 0x71dc30,
  0xd077d, 0xb867a, 0x71f0e0, 0x9e64b, 0x9b23c, 0x71f9d0}` 重建回调指针数组传入。

**修复后里程碑**：`load WoGGGuard` → `DexNative.<clinit>` 通过 → fty so 转译加载执行 →
**壳解密出真实 dex 并加载成功**（壳内部日志「自定义爬虫代码加载成功」）。

### 6.6 新卡点：壳初始化后段 SIGSEGV 空指针（当前唯一堵点）

- 现象：壳日志「自定义爬虫代码加载成功」之后，桥进程退出，**退出码 139 = SIGSEGV**。
- 取证（artlaunch 信号哨兵 trampoline，2026-09-27 晚）：崩溃前**大量 SIGSEGV 正常流转
  处理**（accurate-sigsegv 模式转译运行依赖 fault 流转，链路 OK）→ 致命的一次是
  `si_code=1 (SEGV_MAPERR)、si_addr=0x0`——**解引用空指针**，ndk 判定 fatal。
- 定性：**转译引擎本身工作正常**（壳解密、真实 dex 加载都过了），死因是
  **真实类初始化在极简环境拿到 NULL 依赖**（闭源加固代码没判空直接用）。
  对齐 ChromeOS 完整系统还需要补齐哪块框架支撑，只能逐步试。
- 宿主实测（Windows，CATCLAW_X86_GUEST=1）：与 108 现象完全一致——桥就绪 →
  玩偶 jar 取回（90ms）→ DexClassLoader 就绪 → 壳解密成功 → 39ms 后退出。
- 建议手段：换/补框架支撑面（AndroidRuntime 服务、mount ns、/system 扩展件）；
  LOGD_HEX=1 抓 ndk 静默日志；对照 Bliss 完整镜像逐块补。
- 验收脚本：108 `/root/x86guest/bench_guard.py`（ping → load WoGGGuard →
  homeContent），qemu 日志 tail 看断点；`init_x86.sh` 现在会打桥进程退出码
  （139=SIGSEGV 132=SIGILL 134=SIGABRT）。

#### 2026-09-27 深夜取证进展（黑箱已拆开一半）

**已修的真 bug：sigchain 单槽**。原 `artlaunch.c` 的 `g_chain` 每个信号只存
一个 handler，而 ART 的 FaultManager 与 ndk 转译器**都会**注册 SIGSEGV，互相
覆盖使处理链断裂：guest fault 经 ndk 修正上下文后恢复到无效 PC → 立刻再 fault
→ 循环（内核打印 `ip=fffffffffffffb17 error 15`、同 PC 反复出现就是它）。
已改为真 libsigchain 语义（多槽、后注册者先处理、谁返回 true 谁收敛）。

**取证工具链**（`CATCLAW_SIGLOG=1` 开启）：信号哨兵打印 si_code/故障地址/
**崩溃 PC**（ucontext 的 REG_RIP），并现场解析 `/proc/self/maps` 打印 PC 落在
哪个模块+偏移；桥侧 `Art.loadSpider` 加了 step#3..#11 步骤打点；init 加了
`-Xcheck:jni`。脚本：`run_guard_crash.sh` / `run_guard_siglog.sh` /
`enable_checkjni.py` / `build_gb_dex.py`（只重打 dex，不动现网 initrd）。

**当前结论**：
- 崩溃发生在 **`loadClass` 内部**（壳 `<clinit>` 后半段：打完「自定义爬虫代码
  加载成功」后，连 `step#3` 日志都没出来）
- 崩溃 PC 落在 **`/apex/com.android.art/lib64/libart.so` 的可执行段**（偏移
  0x15f000 起），故障地址非 0 ⇒ **不是「guest 代码解引用 NULL」，而是 ART 自身
  在执行中访问违规**
- 顺带补了 App 桩的 `getSystemService`（造真类 Unsafe 伪实例，绝不返回 null）
  ——本次崩溃并未经过它，但同类坑已消除

**下一步（明线）**：用 108 的 x86_64 binutils 把崩溃偏移反查到 libart 的
具体函数（`addr2line` / `nm`），定位 ART 的哪条路径在转译上下文里跑飞。

### 6.7 非 Guard 源原生速度验收（2026-09-27 晚，✅ 通过）

- 108 `bench_speed.py` / `bench_pick.py`：fty.jar（宿主 NonGuardFallbackJars 同源，
  900+ 非 Guard 爬虫类）→ load(Bili/Auete/AppYsV2/DouDou…) 全部成功（0.06~0.17s）、
  homeContent/categoryContent/detailContent 调用链全部 ok（毫秒级响应；
  数据为空仅因测试源未配 ext，机制本身全通）。
- aarch64 TCG 上聚合网盘源 detailContent 71~90s → x86 WHPX 上同类调用毫秒~秒级，
  **「非 Guard 源原生速度」的核心价值已落地**；宿主测速等接真实订阅 ext 后自然体现。

### 6.8 迅雷引擎与 ART guest 合并方案（2026-09-27 可行性已验证，待实施）

> 用户既定设计（架构图「qemu-aarch64-static（迅雷 aarch64 harness 的 user-mode
> 转译，预留）」）：**迅雷引擎不再独占一个 aarch64 TCG VM，搬进 x86 mini guest**，
> 与 QemuArtGuest 共用同一个 QEMU 实例。

**目标形态**：
```
x86 guest（WHPX 单 QEMU 实例）
├─ artlaunch（ART 桥，18600 行协议）                     ← 已有
├─ qemu-aarch64-static -L /thunder /thunder/harness      ← 新增（用户态转译迅雷）
│    ├─ 监听 :20080（媒体流）→ 宿主 hostfwd（x86 QEMU 已有 media hostfwd）
│    ├─ 回连 10.0.2.2:18080（控制口）→ 宿主 QemuControlServer（协议零改动）
│    └─ 提供 BLK_DEV 口：写 /dev/vdb → 宿主直读（x86 QEMU 加 virtio-blk drive）
└─ /thunder-data 真 tmpfs（ramfs statvfs 不可信，迅雷会静默不下数据）
```

**已验证（2026-09-27 108 实机，qemu-user 直跑）**：
- `qemu-aarch64-static -L <thunder目录> ./harness` 直接跑通 bionic PIE：
  linker64 加载 ✓ 迷你 JNIEnv ✓ DNS(114) ✓ 引擎加载 ✓ 真调用——
  `getDownloadLibVersion()=6.0529.260.26`、`XYVodSDK_getVersion()=2.0.8.15-arm64_v8a`
- `-L` sysroot 模式天然解决 `/system/bin/linker64` 与 `/system/lib64` 绝对路径
- 件体积：qemu-aarch64-static 16.6MB（static-pie）+ thunder 包 ~11MB —— initrd 可接受
- harness 全部参数走**环境变量**（CTRL_PORT/BLK_DEV/PROXY_PORT=20080/QCO=1/
  GUARD_PORT），cmdline 仅传 ctrl=/blkdev=/swapdev=/tdata= —— x86 guest 可 1:1 提供

**收益**（对照 docs/qemu-engine-performance.md）：迅雷引擎从全系统 TCG（2.9%，
19.5 MB/s）升级为用户态转译（7.2%，约 2.5×）；省掉整个 aarch64 VM
（-m 2560 + 4 vCPU TCG 满负荷）；单 QEMU 进程。

**实施清单**：
1. mk_x86_initrd.sh：initrd 加 `qemu-aarch64-static` + `/thunder/`（harness+system libs
   +thunder-data 种子），initrd 预算 ~306MB
2. init_x86.sh：起 harness（env 对齐 + hosts 劫持 4 条 sandai 域名 + /thunder-data tmpfs）
3. x86 QEMU 命令行：btcache storemain/swapmain 两块 virtio-blk + `blkdev=/dev/vdb
   swapdev=/dev/…` cmdline（QemuHostRuntime x86 分支加 drive 参数）
4. 宿主 C#：QemuGuestEngine 增加 x86 分支——复用 ART guest 的 QEMU 实例（共享
   QemuHostRuntime 设备层）；**关键设计点：生命周期耦合**——ART 桥重置当前会
   Stop 整个 VM 连带杀迅雷会话，需改为「ART 重置只重启 guest 内 artlaunch 进程
   （init 监督器），不动 VM」；→ 该监督器改造建议与合并同期做（init_x86.sh 现在
   是串行待机，改为后台 + 监督重启）
5. 验收：磁力全链路（TASK MAGNET→媒体流→块设备直读）+ 性能对照 19.5 MB/s 基线

---

## 7. 未完成任务

- [x] ndk 转译 classloader/线程域问题（2026-09-27 连环攻破，见 §6.5——线程域假设被否，
      真因是注册时序/namespace 判定/g_runtime_callbacks 三连）
- [x] 非 Guard 源原生速度验收（2026-09-27 晚 ✅，见 §6.7——load/调用链毫秒级）
- [ ] 壳初始化后段 SIGSEGV 空指针（取证完成，见 §6.6——补齐极简环境缺的框架支撑）
- [ ] **迅雷引擎与 ART guest 合并**（用户既定设计，可行性已验证，见 §6.8——按实施清单
      5 步走；关键设计点：ART 重置只重启 artlaunch 进程而非整个 VM）
- [ ] 双 guest 路由的 C# 实现（按源分流）——暂缓（非 Guard 已原生，Guard 待 §6.6 突破）
- [ ] WHPX 不可用用户的一键启用引导（设置页，DISM VirtualMachinePlatform）
- [ ] 发行打包（build-win-release.ps1 带 x86 组件，预计 +300MB）
- [ ] Guard 转译打通后的真机验收：玩偶 detailContent 71.6s → 秒级对照
- [ ] 镜像裁剪第二轮：framework boot 分件与 framework-res.apk（需动 boot classpath，风险高一档）
- [ ] proppreload 表清理：native_get(String) 单参版在 13 里非 native（注册必失败，可删）；
      native_find_prop 已改名 native_find(String)J（handle 语义，需重写）

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
