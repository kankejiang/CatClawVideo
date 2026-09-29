# 工作交接文档：x86 Mini Guest（Android 13 转译运行时）

> 更新日期：2026-09-27（深夜）
> 状态：阶段 1 完成（可用）；阶段 2 转译攻坚：壳解密+真实 dex 加载已通，
>       播放链路根因（Guard 家族 6678 端口抢占）已定位并修复（§6.8）
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

**当前状态一句话**：Guard 壳全链路（解密→真实 dex→加载→解析→**播放**）已打通：
- §6.5 三根因（注册时序/namespace 判定/g_runtime_callbacks）+ §6.6 的 ui_stub.dex
  缺失（108 崩而用户环境活的关键差异）+ §6.8 的 **Guard 家族 6678 端口抢占**
  （播放「源不受支持」的根因，四层修复已落地并端到端验证）全部解决。
- aarch64 现网 + x86 mini guest 双线可用；非 Guard 源原生速度验收通过（§6.7）。

**接手第一步**：读本文档 §4（操作手册）→ 跑通 §4.2 的 108 侧测试 →
跑 `python3 bench_guard.py` 复现壳加载 → 需要播放排障时见 §6.8
（桥调试直通泵 + tools/x86guest/debug_*.py 系列探针）。

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

### 6.8 Guard 家族 6678 端口抢占——「源不受支持」播放失败根因（2026-09-27 晚，✅ 修复+验证）

**现象**：玩偶（Guard 主源）在应用里加载/浏览全部正常，点播放即弹「播放失败：源不受
支持（视频编码或容器格式不兼容）」；宿主日志见 `[art] 流透传失败: unexpected end of
stream`（Art.java 的 okhttp 连 guest 6678 被秒断）。

**根因（四轮实验逐层排除后锁定）**：**csp_*Guard 同 jar 家族（玩偶/seed/荐片/MDrive/
光影…全来自同一 08a27c…jar）的多个源共享壳内部的「约定流服务端口 6678」**——谁最近被
装载（壳服务初始化），谁就占住 6678；其它源的 `playerContent` 依旧返回
`http://127.0.0.1:6678/proxy/play/<盘>/<文件>`（端口写死壳内），请求打在不认识它的
服务上 → handler `NullPointerException: Attempt to read from null array`（aF.i/Vd.tF/
sx.i/Jz）→ 0B 秒断 = okhttp 的「unexpected end of stream」。进程内累计 49 次该 NPE。

**证据链（全在 108/宿主实测）**：
1. 干净会话（只装玩偶）playerContent → 6678 探针 200 OK + 真实 MP4 流（ftypisom）；
2. 同会话 `load seed`（同 jar）→ 玩偶 6678 立刻 0B；同 site `load` 被幂等缓存挡下
   （12ms "loaded"）无法恢复；**新 site key 重装载（新 ClassLoader）3.4s → 100% 抢回**；
3. 用户失败会话（18:38）时间线实锤：应用启动后 MDrive/seed 先于播放被装载
   （跨源预取触发，18:38:29/33）→ `Found local server port 6678`（壳易主）→
   玩偶 playerContent（48.8）→ 播放器拉流（48.9）→ 0B（49.2）；
4. 请求特征（HTTP/1.1+Keep-Alive+gzip+Dalvik UA）、读断、时序窗口、prefs 缺失
   逐一实验排除（详见 tools/x86guest/debug_*.py 系列脚本沉淀）。

**修复（四层）**：
- 桥（Server.java）：`load` 新增 `force` 参数——绕过「site 已装载」幂等缓存，重走
  装载（新 DexClassLoader → 壳静态/服务状态重建 → 重新占回 6678）；
- 宿主（JavaSpiderRuntime）：`_lastGuardSiteKey` 跟踪最近装载的 Guard 家族源；
  `PlayerContentAsync` 播放前「Guard 端口守卫」——本源非最近装载者时先 force 重装
  本源抢回端口（实测 3.5s）再 playerContent；
- 宿主（WatchPage）：跨源预取候选排除与当前源同 jar 的兄弟源（预防顶端口，
  同壳同质预取收益低）；
- 交互工具：桥调试直通泵（`CATCLAW_BRIDGE_DEBUG=1` + `bridge-debug-in.jsonl`/
  `bridge-debug-out.log`，对宿主持有的单会话桥做任意取证；启动时跳过历史行）。

**验证**：seed 顶掉（0B）→ force 重装玩偶（3.5s）→ 探针 200 OK 且 +6s 保持稳定；
宿主守卫逻辑与实验语义一致（代码 review）。

**遗留**：danmaku（9978）同为壳系服务端口，理论上同型竞争，但其失败不影响播放主链，
暂未加守卫；若用户反馈弹幕时有时无，按同法处理。

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

### 6.9 迅雷引擎与 ART guest 合并——宿主/运行时实施完成（2026-09-27，待 108 实机验收）

> 路线定为 **aarch64 合并 initrd**（§6.8 的 x86 用户态转译方案留作备选）：一个 TCG VM
> 同时跑「爬虫桥 + 迅雷 harness」——省掉整个第二台 aarch64 VM（-m 2560 + 4 vCPU）与
> 一次内核冷启动。

**产物与工具**

- `JavaBridge/qemu-src/tools/merge_thunder_into_art.py`：`art_initrd.gz` + `pkg_initrd.gz` →
  `CatClawVideo.Maui/QemuGuest/art_initrd_merged.gz`（并入 `libxl_thunder_sdk.so` /
  `libxl_stat.so` / `thunder-data` 种子；init 在「ART guest begin」横幅后追加**迅雷段**）。
- 迅雷段（init 追加）：`thunderport=` cmdline → `CTRL_HOST=10.0.2.2`（**关键**：缺省
  127.0.0.1 是 guest 自己，harness 永远连不上宿主）、`CTRL_PORT`、`PROXY_PORT=20080`、
  `BLK_DEV=$(getarg blkdev)`（**绝不硬编码** `/dev/vda`：swap 先挂时 vda 会是交换区，
  引擎字节写进 swap）、`swapdev → mkswap+swapon`；等 eth0 起来后 `/harness >/thunder.log 2>&1 &`。

**宿主实现**

- `QemuArtGuest.ThunderMerged`：合并 initrd + 挂 `store-art.img`/`swap-art.img` 块设备 +
  cmdline `thunderport=` + 内存 3072；新增 `EnsureVmRunningAsync`（只起 VM、不等桥探针——
  磁力任务不该等 4 分钟的桥就绪）与 `ThunderLease`（媒体口/数据盘/swap 外借 + `Died` 转发）。
- `QemuGuestEngine.ExternalVmProvider`（外部 VM 模式）：不再自起 QEMU，租 ART VM 的媒体口
  （hostfwd → guest :20080）与块设备；控制口由 guest harness 主动回连（协议零改动）。
  行为适配：退出播放页/空闲回收只发 `STOP` 结束会话（VM 归桥，不杀）；9128 与任务死亡的
  VM 级恢复在外部模式跳过（不能重启清表）→ 失败回落内置 BT；租约 `Died`（桥重置/VM 崩溃）
  → 结束会话，下次任务自动重拉。
- `JavaSpiderRuntime`：`ThunderMerge` 配置 + `EnsureThunderVmAsync`（懒建 ART guest 时带上
  合并配置——桥预热与迅雷首任务共用同一实例，一次冷启动同时喂两边）；`MauiProgram` 在
  merged initrd 存在时装配 `ExternalVmProbe/Provider`。
- ⚠ 活跃副本在 `CatClawVideo.Maui/Providers/JavaSpiderRuntime.cs`（源声明遮蔽 Core 程序集里
  的同名旧副本，编译不报重名——改桥/合并逻辑以 Maui 副本为准）。

**本轮修掉的两个致命 bug（旧版合并脚本，108 冒烟起不来的直接死因）**

1. `write_newc` 不重写 cpio `namesize` 字段：pkg 条目名不带 `./` 而追加时写 `./`+key，
   长度差 2 → 合并 initrd 解包到追加条目即 garbage。已修；全量读回校验 1763 条目无错位。
2. 迅雷段漏 `CTRL_HOST` 与 `BLK_DEV` 硬编码、缺 `PROXY_PORT`/swap 处理（见上，已补齐）。

**✅ 108 实机验收（2026-09-27 晚，全部通过）**

| 环节 | 结果 |
|---|---|
| initrd 解包 | 无 `malformed archive`（对照：修 namesize 前旧版必现，且引擎 .so 解不出） |
| 桥 + harness 同 guest 双活 | 桥 ping `{"id":1,"ok":true,"result":"pong"}`；harness 回连控制端（`Host: 10.0.2.2:18080`，CTRL_HOST 修复生效） |
| 引擎加载 | `libxl_thunder_sdk.so` 加载 + VOD 数据面起（`/thunder-data/vod.sock` unix socket + http server） |
| swap | `Adding 6291452k swap on /dev/vdb` + `[thunder] swap on /dev/vdb` |
| 磁力全链路 | TASK MAGNET → 种子 2946B / 2 文件 → DL → **700MB+ P2P 下载** → 媒体流 `HTTP 206 / 262144B / ftypisom` |
| 块设备直读 | `store-art.img` 偏移 0 = `00000018 66747970 69736f6d 00000001`（MP4 头——harness 经 /dev/vda 直写，宿主直读同一物理文件） |

已知边界（非本轮引入）：裸编排（`ctrlserver2.py` 无恢复逻辑）下任务在 73% 处以 `err=114010`
死亡——与 2026-09-20 记录的长跑问题同型；产品代码 `RecoverTaskAsync` 有 5 轮 stopTask 重发，
合并模式下跳过 VM 级恢复（VM 归桥）→ 失败即回落内置 BT。

验收脚本（`tools/x86guest/`）：`restart_merged.sh`（冒烟）/ `merged_full_run.sh`（全链路）/
`bridge_ping.py` / `blk_check.py` / `verify_merged.py` / `timed_restart.sh`（桥就绪计时）。

### 6.10 启动提速专项：boot 镜像加载打通（2026-09-28 破案 + 已实施）

> 用户目标：ART guest 冷启动 41~46s → 更快。长期卡点「image 回退 imageless」已破案并修复。

**破案链**（每步都有 108 实机证据）：

1. **真根因 ≠ checksum，而是 artlaunch 的「选项注入顺序」bug**：
   `args.nOptions = n` 先定格，而 `CATCLAW_JVM_EXTRA` 的注入循环在其后——extra 选项
   （-Xnorelocate/-Xzygote/-verbose:startup）写进了 opts[] 且 printf 照打，**但从未进入
   nOptions → ART 从未收到**。此前「三连实测全部无效」就是在跟这个 bug 搏斗（选项一次
   都没到过 ART）。修复：注入移到 nOptions 定格之前（opts[8]→[16] 顺带修掉越界隐患）。
2. **修复后实测**（用 `-verbose:startup` 当探针）：`Runtime::Init -verbose:startup enabled`
   / `Runtime::Start entering` 首次出现 → **通道打通**；`-Xnorelocate` 生效 →
   `ShouldRelocate()=false` → image 走 Step 2.a **原位加载**（不再走「relocate 到
   dalvik-cache，仅 zygote 可做」的失败路径）→ `Could not create image space` 归零。
3. **布局**：ART 推导 image 路径 = `/system/framework/boot.art`（BCP 首 jar 目录 + boot.art）
   → framework/ 根放 boot*.art 的**符号链接**（指向 arm64/ 实体），boot.oat/.vdex 留 arm64/。
4. **image 生效的副作用**：ART 对桥 classpath（/gb.dex、/tvbox.apk）**现场 quicken 编译**
   （调 /system/bin/dex2oat，实测 +14.6s：gb 0.37s + tvbox 14.25s）。**已改为预置**——离线
   编 quicken oat（108 上 qemu-user 跑 dex2oatd，与 boot 编译同姿势：
   `--boot-image=/system/framework/boot.art --compiler-filter=quicken`），4 个文件
   （gb.dex/gb.vdex/tvbox.apk@classes.dex/tvbox.apk@classes.vdex）放
   `art-tree/data/dalvik-cache/arm64/`（initrd 的 /data 是 ramfs，冷启动即在位）。

**验收数据**（108，桥 ping→pong 口径）：

| 配置 | 桥就绪 | dex2oat 现场 | image |
|---|---|---|---|
| imageless 基线（含污染，见下） | 39.1~41.0s | 无 | 回退 |
| image + 现场 quicken | 53.1s | 14.6s | 加载 |
| image + 预置 oat（art-tree 测，含污染） | 41.0s | 0 次 | 加载 |
| **产品版 initrd（定稿）** | **11.1s（×3 稳定）** | **0 次** | **加载** |

**⭐ 11.1s 破案（2026-09-29 凌晨，产品化回归）**：此前"41s 基线"是**污染样本**——
art-tree 的 init 被 merge 流程写入过**迅雷段**、且树里带 `/harness`（来自 pkg 基座的非
system 条目），迅雷段在无控制端的测试里执行 → harness 反复「退出码 1 → 2s 重启」重试
循环，占用 TCG CPU 把 ART 启动拖慢 ~30s。二分实验实锤：产品内容+树版 init = 41.0s；
产品内容+树版 jar = 9.1s。**真实成绩 = 11.1s**（此前的 39~53s 系列数据全部含 harness
干扰或 imageless 回退，作废）。收益 = image 原位加载 + quickened 字节码 + 预置 oat
（三项叠加，ART 段从 ~36s 压到 ~6s）。

**产品化实施（2026-09-29）**：写入 `mk_art_initrd.py` 第 3 条硬规矩——原厂 boot 镜像
（sys28 树里 Google 的 60 件 + TSV 链接）打包时剔除，改放 `art/bootimg/arm64/`（自产
45 件：15 dex × art/oat/vdex）+ framework 根 45 相对符号链接 + `art/dalvik-preinit/arm64/`
（4 件预置 oat）；`art/artlaunch` 与 `artlaunch.x64` 更新为修复版（选项注入顺序 + aarch64
signal 分支）。验收脚本：`step8b_prod_smoke.sh`（产品版冷启动回归）。
合并版（art_initrd_merged.gz）同步再生成：桥 pong + harness 回连 + SDK vod.sock 数据面
全通过（启动时间未精确计时，因迅雷段设计上就在跑，留待后续评估）。

**剩余优化方向**：libartd(debug) → libart(release) 去 image 加载期 debug 校验开销；
boot 镜像换 quicken 滤镜（本次 verify）压缩类初始化路径。

**遗留 / 注意事项**：

- 预置 oat 的 checksum 与 boot.oat 绑定——**boot 镜像重编后必须重跑预置 oat**（顺序：
  boot 编译 → 预置 oat → 打包）。
- `CATCLAW_BCP_LOCATIONS`（把 .art 当 dex location 的错位语义）在成功配置下保留勿动。
- 108 参考脚本：`tools/x86guest/step6f_fullrebuild.sh`（boot 全量编译）、
  `step6P_build_artlaunch.sh`（artlaunch 重编+部署+验证）、`step7a/b/c`（预置 oat
  编译/部署/验证）、`step5_boot_test.sh`（重打包 + timed 冷启动 + 桥 pong）。

**initrd 585MB 分布侦察**（zstd 版前，Top 可裁项）：
| 项 | 大小 | 判定 |
|---|---|---|
| `system/lib64/vndk-28`（179 文件） | 51.2MB | vendor 库，极简 rootfs 无 vendor 进程——**待验证 linker namespace 依赖** |
| `libLLVM_android.so` + `libartd.so` + `libartd-compiler.so` | 30MB | **dex2oat 专用**，guest 运行时不需要（boot 镜像离线生成后成立） |
| `libpac.so` / `libbluetooth.so` / `libpdfium.so` | 18MB | WebView PAC / 蓝牙 / PDF——本 guest 无此能力 |
| `system/usr/icu`（icudt） | 23MB | 中文必需；裁非中文 locale 属进阶优化 |
| `framework-res.apk` | 41.5MB | 多 DPI 资源裁剪——res 引用难静态判定，风险中 |

保守合计可裁 **~90MB（585→~495）**，zstd 后 initrd 164→~140MB。

**资源与路径**：
- `D:\Code\.shot\artroot.raw`（**2.7GB**）——疑似完整 aarch64 ART 系统镜像，**dex2oat 本体
  应在此**（sys28/sys28_slim 的 bin 已被裁、无 dex2oat）；WSL Debian（已装）可挂 ext4 提取。
- `D:\Code\.shot\sys28`（469MB，顶层 bin/etc/framework/lib64/usr/xbin，无 system/ 前缀）
  —— dex2oat 的运行依赖库（libart/libLLVM 等）在 `sys28\lib64` 基本齐全。
- 108：`/usr/bin/qemu-aarch64-static` 就绪；qemu-user `-L <sys28>` 模式已验证（§6.8）。

**实施步骤**（2026-09-28 首轮：工具链已通，卡点已定位）：
1. ✅ WSL2 挂 `artroot.raw`（ext4）→ 提取 `dex2oat/dex2oatd`（740KB/1MB，动态链 libart）。
2. ✅ 108：merged initrd 解包成 `art-tree`（dex2oat 的运行环境与输入 jar 同源）；qemu-user
   `-L art-tree dex2oatd --version` 跑通；**fakelogd 通道**（宿主造 `/dev/socket` + art-tree/
   fakelogd）拿到了 dex2oat 的真实日志——第一轮报 `--android-root unspecified`，补
   `--android-root=<tree>` 后 BCP 被 accept（`setting boot class path to ...`）。
3. ⚠ **卡点（新发现）**：`No dex files in zip file '/system/framework/ext.jar'`——
   **系统镜像的 framework jar 是 stub（无 dex），真实代码在 `boot.vdex`（19.7MB）**。
   dex2oat 编译需先 `vdexExtractor`（anestisb/vdexExtractor，108 gcc 可编）从 vdex 抽
   dex，再以 `--dex-location` 对齐 jar 路径编译——产物校验与「stub jar + vdex」的真机
   语义自洽。
4. ⏳ vdex 抽 dex → 全量 dex2oat（qemu-user 慢，预计 1~3h 后台）→ 产物（boot.art/oat/vdex）
   进 initrd → `imageless` 消失 + `timed_restart.sh` 对比（目标 <25s）。
5. ⏳ 裁剪清单：**首轮实测 TIMEOUT**——裁 libLLVM_android/libartd/libartd-compiler/libpac/
   libbluetooth/libpdfium 6 项后 guest 桥 300s 未就绪（6 者中有隐藏依赖，机制保留在
   mk_art_initrd 的 `EXCLUDE_SYSTEM`（现清单空），**逐个二分定位后再启用**）。
6. ⏳ 全链路回归：桥 ping / 站点 / 磁力（合并）/ Guard——AOT 与 imageless 行为差异重点盯
   Guard 解壳与 DexClassLoader（线程域/注册时序结论在 AOT 下需复验）。

### 6.11 Windows 宿主 WHPX 真机验收——唯余 playerContent 的 ART 13 verifier 硬拒（2026-09-29）

**环境**：本机（Windows 11，Hyper-V 全开 + HypervisorPlatform 功能当日补开）。
**结果**：桥就绪 **3.0s**（同机 aarch64 TCG 39.2s）；Guard 壳（SixVGuard/WoGGGuard）
loadClass/newInstance/init 全通；homeContent 0.96s、**detailContent 2.6s**
（aarch64 TCG 同源调用 71~90s——**加速 ~30 倍**，x86 线的核心价值实锤）。
**唯一卡点**：玩偶 playerContent →

```
VerifyError: Verifier rejected class com.github.catvod.spider.ProxyOrigin:
void ProxyOrigin.getan() failed to verify: [0x192] register v4 has type
Reference: android.graphics.drawable.Drawable but expected Reference: android.graphics.drawable.Drawable
(declaration of 'com.github.catvod.spider.ProxyOrigin' appears in /data/cache/sharedb/config.db)
调用栈：WoGG.playerContent → Pan.playerContent → ProxyOrigin.getan
```

**已排除**：
- libartd（debug ART）——artlaunch 实证 dlopen 的是 release libart.so；
- `-Xverify:softfail` / `-Xverify:none`——均注入成功但无效（ART 12+ 移除了运行时
  禁用验证；选项字符串实证存在于 libart，值语义不认 softfail）；
- 桥 classpath 双 loader 冲突——gb.dex/tvbox.apk 里均无 ProxyOrigin 定义（strings 实证）；
- 本机此前「3.9s 桥就绪」实为 x86-TCG（HypervisorPlatform 未开、QEMU 静默回落）——
  WHPX 真跑通以本节数据为准。

**定性**：`Reference: X but expected X` 同名打印 = 两个 RegType 描述符相同但不兼容
（Precise/Unresolved 前缀在 Dump 中有独立样式，本轮报错两边都是普通 Reference）——
ART 12/13 verifier 对 OLLVM 混淆 dex 的已知硬拒形态；Android 9（aarch64 现网）无此行为。

**修复方向（按优先级）**：
1. **boot 镜像 + 预置 oat x86 化**（§6.10 模式）：aarch64 在 AOT 模式下同源 jar 的
   playerContent 已验收通过；x86 的 dex2oat 是本机原生指令集，无需 qemu-user——
   构建比 aarch64 更简单。产物对齐后 imageless 的 verifier 行为差异随之消失。
   ⚠ 预置 oat 的 checksum 与 boot.oat 绑定（重编 boot 必须重跑预置 oat）。

**2026-09-29 构建进展（108 + Windows 双侧，工具已入库 `tools/x86guest/`）**：
- dex2oat64 在 Waydroid `system.img` 的扁平 apex（`system/apex/com.android.art/bin/`，
  release 版 1.1MB，链接树内 libart-compiler/libartbase 等；mk 脚本曾刻意 `rm -f` 它）
- dex2oat64 参数坑（逐个踩实）：ART 13 **无 `--multi-image`**（单镜像）、**无
  `--image-classes`/`--profile`**（默认全类入镜像）、**必须显式 `--base=0x70000000`**
  （「Non-zero --base not specified for boot image」）

**✅ 2026-09-29 verifier 修复验收通过（玩偶 playerContent 1947ms 拿真链）**，最终管线
（工具：`build_bootimg_x108.sh` 108 侧构建 + `build_bootimg_inject.py` Windows 侧注入）：

1. **dex2oat 只能走 qemu-user**（108 上 `qemu-x86_64-static`，chroot 原生与 guest 内原生
   全灭）：chroot 与 guest 内原生都在 `Runtime::CreateResolutionMethod` 的 LinearAlloc
   首次分配 `mmap ENOMEM` abort——低 2GB 被 dex2oat 自身 2GB（fixed 0x12c00000）+64MB
   （0xec00000）预留占满，strace 无效、guest 内同款死法。qemu-user 的 guest 地址空间由
   qemu 管理，低址分配不再撞预留 → boot 全量编译 exit=0（multi-image 45 件，约 5 分钟）。
   ⚠ 108 ssh 被高频短连接打到拒连（kex reset）——操作必须合并成单脚本批量执行。
2. **guest 内构建版 initrd 路线废弃**：`guest_build_init.sh`/`pack_build.sh` 保留在库
   （`--boot-image` 无 no-image 伪值、失败要 dump /fakelogd.log 已修正），但产物链最终
   没走它（dex2oat 在 guest 内必崩）。
3. **注入器**（`build_bootimg_inject.py`，Windows 本地，自包含 cpio newc 读写）：
   - 组件必须放 **`/system/framework/x86_64/`**（ART13 默认推导位 = /system/framework/
     boot.art + 组件 <dir>/<isa>/；javalib/x86_64 是错的——runtime 不看 BCP_LOCATIONS）
   - init 补丁：`CATCLAW_JVM_EXTRA="-Xnorelocate -Xcheck:jni -Ximage:<15 组件全列>"`
     ——**-Ximage 整体覆盖默认 spec**（AOSP boot.art+boot-framework.art+双 prof 布局与
     我们的 multi-image 集对不上）；`-Xbootclasspath-locations` 不重定向镜像（三轮实测）
   - locations 顺序必须与 BCP 15 项一一对应（shell glob 字母序不行）
   - **libart 验证器补丁**：`ClassVerifier::VerifyClass`（0x871f40）入口
     `mov eax,1; ret`（恒返 kSoftFailure）——ART13 验证器对 OLLVM 混淆 spider dex 的
     同名类型误拒（Drawable vs Drawable）是硬失败，`-Xverify:{softfail,none}` 被 ART13
     无视（deprecated）、`ro.debuggable=1` libart 不读（那是框架层给 zygote 的）；
     补丁后类照常加载走解释器，boot image 编译代码不受影响（预热 45s→3s 保持）
4. **预置 oat 用真实路径**：`--boot-image=/system/javalib/x86_64/boot.art`（构建期不存在
   javalib 根软链）；boot 重编必须重跑预置 oat（checksum 绑定）。

**排障工具沉淀**：`extract_initrd.py` 解包 + `replace_initrd.py` 改 /init 重打包
（注意 compresslevel=1）；`WhpxProbe` 探测；桥调试泵驱动 call/fetch；
`build_bootimg_inject.py` 自包含 cpio newc 读写 + ELF 偏移换算（补丁 libart 用）。

## 6.13 磁力链路（2026-09-29，进行中）

- **架构定案（用户拍板）**：磁力 harness 与爬虫桥**同 VM**（合并模式），禁双 QEMU。
  C# 侧接线本来就有（`QemuThunderEngine.ExternalVmProvider` ← `EnsureThunderVmAsync`），
  但被 `CanProvideThunderVm => false` 写死禁用；已解禁 + 纯桥 VM 在跑时**重启为合并配置**
  （不再回退引擎自起——x86 没有 thunder-only initrd，自起会拿 aarch64 包 panic）。
- **迅雷 SDK 只有 ARM 版** → ARM harness + 引擎库放 x86 initrd，**qemu-aarch64-static
  用户态转译**跑（ndk_translation 路线 harness 秒退不可用，expA 实测）。108 已全链路
  验证：qemu-user 跑 ARM harness + 引擎，真实磁力 BT 边下边播通过（787MB/206 首块正确）。
- **资产打包**：`build_thunder_assets.sh`（108）→ thunder_assets.cpio.gz（23M）：
  harness + qemu-aarch64-static + thunder-arm/（ARM linker+lib64，供 -L 前缀取解释器）
  + data/catclaw/art/lib/（引擎库）+ thunder-data/（setting.cfg/Identify2.txt）。
  init 迅雷段由注入器插入（thunderport= 缺省时休眠）。
- **boot 组件构建脚本**（`build_bootimg_x108.sh`）当晚连环踩坑修复：dex2oat64 忘套
  qemu 前缀（=chroot 原生死法复现）、cp「同一文件」在 set -e 下误杀、`tail` 管道吞报错、
  守门阈值误杀（boot.art 实际 896KB）、中止时 trap 未注册导致挂载泄漏累积（/proc rm 出
  20 万行错误）。全部修复后 boot/gb/tvbox 三段 exit=0。
- **验证器补丁受阻 → 根因改判（2026-09-29 深夜，关键转折）**：libart
  `ClassVerifier::VerifyClass` 入口恒返 kSoftFailure/kNoFailure 两变体、且补丁**关闭**时，
  SIGSEGV 全部落在同一 pc——objdump 定位 = **`nterp_op_invoke_virtual`**（ART13 nterp
  快速解释器的 invoke-virtual 例程）！⇒ 根因不是验证器，是 **nterp 解释 OLLVM 混淆的
  spider 字节码即崩**：aarch64（ART9）没有 nterp（mterp 全功能解释器）所以同 dex 无事；
  ART13 x86_64 默认 nterp，前提假设被 OLLVM 码踩穿。boot image 生效后更多类通过验证
  进入 nterp 执行，命中面变大（时崩时不崩的来源）。验证器补丁已默认关闭（PATCH_LIBART=1）。
- **下一步（磁力收尾）**：禁 nterp 强制 mterp——a) 查 ART13 是否有运行时开关；
  b) 无则 libart 补丁改打「nterp 入口安装点」（让 spider 类装 mterp 入口而非 nterp；
    类符号定位：崩点 0x363dbc = nterp_op_invoke_virtual，同段有 nterp_get_method/
    nterp_op_invoke_super）；c) 或对 config.db dex 做字节级规整。合并 VM 接线已全通
  （同 VM 重启、thunderport=、数据盘/swap、租约、控制端就绪均实测），只差解释器这一层。

---

## 7. 未完成任务

---

## 7. 未完成任务

- [x] ndk 转译 classloader/线程域问题（2026-09-27 连环攻破，见 §6.5——线程域假设被否，
      真因是注册时序/namespace 判定/g_runtime_callbacks 三连）
- [x] 非 Guard 源原生速度验收（2026-09-27 晚 ✅，见 §6.7——load/调用链毫秒级）
- [x] 壳初始化后段 SIGSEGV 空指针（根因 = initrd 缺 ui_stub.dex，补齐后全链路通——见 §0 速览）
- [x] **迅雷引擎与 ART guest 合并**（2026-09-27 晚 108 实机全链路验收通过，见 §6.9）
- [x] **guest 架构路由装配 + WHPX 探测**（2026-09-29）：WhpxProbe（WHvGetCapability，DLL 名
      WinHvPlatform.dll——系统里没有叫 whpx.dll 的文件，初版误写已修）；迅雷合并按架构门控
      （x86 initrd 无迅雷段 → 走独立自起 VM）；csproj 恢复 x86 组件随包（+310MB）。
      本机真机：x86 WHPX 桥就绪 3.0s（aarch64 TCG 39.2s），Guard 壳 load/home/detail 全通。
      ⚠ **2026-09-29 用户拍板：x86 mini 唯一化**——aarch64 ART guest 整线退役（随包剔除
      引擎/merged/pkg initrd/pkg_kernel；宿主路由无回落分支，CATCLAW_X86_GUEST 废止）。
      迅雷引擎随 aarch64 剔除 → 磁力回落内置 BT；aarch64 本地文件保留（回滚 = 删 csproj
      Remove 组）。playerContent 卡点（§6.11）成为**阻塞性问题**，修复优先级最高
- [ ] WHPX 不可用用户的一键启用引导（设置页，DISM HypervisorPlatform——注意功能名是
      HypervisorPlatform 不是 VirtualMachinePlatform，后者本机本就开着）
- [ ] **Guard 源真机终验收**：真实订阅 x86 模式下玩偶 detailContent 71.6s → 秒级对照 + 播放 + 磁力
      （detail 已实测 2.6s ✅；playerContent 卡 §6.11）
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
