# x86 mini guest（Android 13）——架构、联调状态与续跑手册

> 目标：把 ART guest 从 aarch64 TCG（慢 10~20 倍，聚合网盘源 detailContent 71~90s 实测）
> 切到 x86_64 + WHPX 硬件虚拟化（接近原生），复用现有桥/桩/协议全套。
> 应用宝电脑版（x86 Android + ARM 转译）跑同一 jar <10s 已实测——本路线即开源件复刻。

## 架构

```
Windows 宿主
├─ CatClawVideo（.NET）
├─ qemu-system-x86_64.exe <— WHPX 硬件加速（-accel whpx，不可用自动落 TCG）
│    └─ guest：Debian 6.1 内核（binder_linux.ko）
│         ├─ Android 13 rootfs 子集（Waydroid LOS20 x86_64 抽取）
│         │    ├─ /system/{framework,lib64,bin,etc} + /apex/{art,i18n,conscrypt,runtime,statsd,tzdata}
│         │    └─ /system/bin/artlaunch → JNI_CreateJavaVM → 桥（行协议）
│         └─ qemu-aarch64-static（迅雷 aarch64 harness 的 user-mode 转译，预留）
└─ JavaSpiderRuntime <— TCP 127.0.0.1:18600 行协议（与 aarch64 guest 完全同协议）
```

## 联调状态（2026-09-27，108 主机 KVM 实测）

| 层 | 状态 |
|----|------|
| 内核 KVM 启动 + 10 模块全装 | OK |
| eth0 / 网络 | OK |
| ld.config.txt 被 linker 读取（手写静态非隔离配置） | OK |
| proppreload 93 条属性接管 | OK |
| nativeloader 显式初始化 + libart 完整加载 | OK |
| dex2oat64 现场生成 boot 镜像 896ms（已用双选项禁用，见下） | OK 实证原生速度 |
| com_android_art namespace | 修复已写入 108，待验证 |
| 桥握手 → detailContent 秒级 | 待验证 |

## 已踩实并写进脚本的坑（必读）

1. 裁剪只能用闭包法：黑名单删 libpdfium → libandroid.so 静态链接它 → panic。
   （tools/slim_sys28.py，libpdfium 事故 2026-09-26）
2. 删宿主 DLL 必须全量互扫+启动验证：needle 漏 libavif → qemu 起不来。
3. set -e + 部分缺失文件：mk 半途退出、rootfs 半成品、测试一直跑旧 initrd
   ——每步产物要校验（javalib 拷贝坑，2026-09-27）。
4. Windows scp 丢执行位：/init EACCES——组装时统一 chmod。
5. 13 的 APEX 化：ART/core jar 在 apex 里 → Waydroid 已 flattened；BCP 走 /apex 路径
   会触发 apex linker namespace 要求 → core jar 拷到 /system/javalib 绕行（2026-09-27）。
6. dex2oat 现场生成的 boot 镜像要求 com_android_art namespace 与 APEX 元数据一致
   → 极简 rootfs 配不齐 → -Xnoimage-dex2oat -Xnodex2oat 双选项禁用，走 imageless+JIT
   （WHPX 原生 CPU 下可接受）。
7. ld.config 自定义 namespace 必须先 additional.namespaces = <name> 声明。

## 续跑手册（108 主机）

```bash
ssh root@10.0.0.108 "bash /root/x86guest/continue_x86.sh"
# 通过后：Windows 侧设 CATCLAW_X86_GUEST=1 启动应用即切 x86 guest
# Guard 转译（阶段 2）：libndk 集成实测；不通过则双 guest 路由
```

## 分发布局（启用时）

```
QemuGuest/
├─ qemu-system-aarch64.exe / qemu-system-x86_64.exe（依赖 DLL 完全重合，实测）
├─ pkg_kernel / pkg_initrd.gz          （aarch64 迅雷，不动）
├─ art_initrd.gz / art_initrd_x64.gz   （双 guest initrd）
└─ x86guest/vmlinuz-6.1.0-50-amd64     （Debian 内核）
```

## 文件索引

- tools/x86guest/mk_x86_initrd.sh —— initrd 组装（108 主机跑）
- tools/x86guest/init_x86.sh —— guest /init（Android 13 适配版）
- tools/x86guest/ld_config_x86.txt —— 静态 linker 配置基底
- tools/x86guest/continue_x86.sh —— 一键续跑（防重追加+组装+测试+诊断）
- tools/x86guest/run_x86_test.sh / diag.sh / diag2.sh —— 测试与诊断
- tools/slim_sys28.py —— aarch64 sys28 闭包裁剪（libpdfium 事故修正版）
- C# 接线：JavaSpiderRuntime（CATCLAW_X86_GUEST=1 实验开关，默认关）
## 阶段 2 转译攻坚日志（2026-09-27，进行中）

ndk_translation（ChromeOS sdk_gphone_x86_64:13 官方抽取，supremegamers 仓库 11arm_13arm64 分支，prebuilts 约 42MB）已并入 rootfs：

| 项 | 状态 |
|----|------|
| gen_props.py 架构化（x86_64 → props_gen_x64.h，nativebridge 属性组） | ✅ |
| proppreload.c 宏选属性表（-DX86_GUEST），157 条在跑 | ✅ |
| artlaunch 探针：3 个 nativebridge 属性全部可达 | ✅ |
| dlopen libndk_translation 主库 | ✅ 依赖齐全 |
| LoadNativeBridge（手工补，libart 只 Load 不 Init） | ✅ =1，state kOpened |
| InitializeNativeBridge(env,"arm64")（正确签名+时机） | ❌ =0，转译器回调失败，日志被 fakelogd 吞 |
| PreInitializeNativeBridge（exec arm64 wrapper） | ❌ 双架构 linker config 深水区，artlaunch 崩溃 → 已回退 |
| binfmt_misc 注册（挂载点必须在 rootfs /binfmt_misc，procfs 不支持 mkdir） | ✅ 基础设施就绪 |

结果：**非 Guard 源在 x86 guest 完全可用（原生速度）**；Guard 源暂由 aarch64 guest 兜底。

下次路线：
1. fakelogd 打印完整 priority/tag/msg → 拿到 ndk initialize 的真实失败原因（当前只有 tag）；
2. arm64 wrapper 的 ld.config 双架构分离（arm64 linker 读到了 x86 的 ld.config.txt 抓错库）；
3. binfmt 基础设施已就绪（arm64_exe 注册串在 ndk 包 etc/binfmt_misc/，runner 在 bin/ 顶层）。
### 2026-09-27 续：死因闭环（ndk arm64 loader 搜索路径）

- fakelogd 重写为协议级解析：logdw 报文 prio@offset11/tag/msg 全量输出（原抽串模式把
  ndk 的关键日志全吞了），hex 诊断模式（LOGD_HEX=1）可 dump 原始报文
- ndk 属性组按 mk 文件补齐：ro.vendor.enable.native.bridge.exec64=1、
  ro.ndk_translation.version=0.2.3、ro.ndk_translation.flags=accurate-sigsegv
- binfmt 挂载点修正（rootfs /binfmt_misc——procfs 不支持 mkdir）→ 注册成功 →
  PreInitializeNativeBridge = 1（binfmt+runner 链路打通）
- 伪造 cpuinfo 覆盖文件（/system/etc/cpuinfo.arm64.txt 与 /system/lib64/arm64/cpuinfo，
  ndk initialize 要 bind-mount 到 /proc/cpuinfo，ChromeOS 构建时生成、ndk 包未携带）
- **死因闭环**：PreNB 拉起的 arm64 wrapper（app_process64）由 ndk arm64 loader 解析，
  搜依赖 libc++.so 时先命中 /system/lib64 的 x86 版 →「EM_X86_64 instead of
  EM_AARCH64」FATAL → 进程树连坐（artlaunch 崩）。bionic 遇架构不符不继续搜索；
  LD_LIBRARY_PATH 不被 ndk loader 尊重；ld.config 插 arm64 路径会引发更早的 linker
  DEBUG 失败（x86 侧解析 config 即崩）。

下次正道：读 google/ndk_translation 开源 mirror 的 loader 路径逻辑（arm64 目录的约定
或开关属性），或为 arm64 wrapper 单独生成一份 ld.config（双架构分离——ndk runner
对 config 文件名的约定需从源码确认）。
### 2026-09-27 再续：arm64 专用 ld.config 打通半程

- 找到 ndk arm64 linker64 的 config 选择链（strings 实锤）：/system/etc/ld.config.arm64.txt
  优先于 /linkerconfig/ld.config.txt（x86 内容）——按 ISA 分离配置是官方机制
- 第一版 arm64 config 解析失败（`section "system" not found`——缺 [system] 段头），补上后：
  PreNB=1 → ndk Initialized → 桥可用 → **主线程 SystemProperties.get 自测成功**
- 卡点再进一层：主线程注册生效，但经 DexClassLoader 加载的 app dex 类调用同一
  native_get 仍报 No implementation found——嫌疑指向 ndk 侧 classloader namespace
  （arm64 类加载器命名空间）对 boot 类 native 注册表的可见性，或 ChromeOS 容器
  的 mount ns 假设。配置已对齐 Bliss/ChromeOS 公开资料，差异在完整系统环境。

三条路线的成本评估：
A. 继续啃 ndk（需拿到 ChromeOS 官方系统镜像抽完整调好的 /system 对照；或逆向闭源 loader）
B. 双 guest 路由落地（x86 跑非 Guard 源原生速度 + aarch64 兜 Guard；工程量小、价值立现）
C. 等待/寻找 ndk_translation 的社区完整实践（Waydroid/Bliss 社区跟进）
### 2026-09-27 三续：官方镜像对照完成，卡点推进到「线程域」

对照基准：Android 13 API 33 官方模拟器镜像（google_apis/x86_64-33_r12.zip，1.5GB，
与 ndk 包指纹同源）。解包链：GPT → 7z 解 LP super → 7z 解 ext4（7-Zip 25.01 全程原生
支持，simg2img/mount 均不需要）。

比对结论（好于预期）：
- 官方 lib64/arm64 恰好 59 个库，与我们拷的 ndk 包**完全一致**，无缺库
- 官方 ld.config.arm64.txt 全文已采纳（${LIB}/arm64/bootstrap 预留路径、visible、
  com_android_neuralnetworks fake APEX namespace、dir.system 含 /data）
- cpuinfo.arm64.txt 已用官方版语义（我们伪造的等价）
- 文件/配置层全部对齐后，arm64 loader 的 EM 架构 FATAL 彻底消失

新卡点（比昨天深一层，且已半解）：
- 主线程：RegisterNatives 注册生效（SystemProperties.get 自测返回正确值 + native_get
  直调也成功）——「注册不生效」假设被否
- DexClassLoader 链（壳 so 静态初始化发起的 JNI 调用）：同一 native_get 报 No
  implementation found——嫌疑收窄到「转译执行线程的 JNI 查找域」或「类加载器视角」
- artlaunch 已埋好主线程/新线程对照实验（pthread + AttachCurrentThread），新线程
  的输出尚未在串口出现（实验块待排障），这是下一步第一件事

下一步：
1. 排障线程实验块的输出（加块入口 printf 分步定位）→ 拿主线程 vs 新线程对照数据
2. 若新线程失败坐实：ndk initialize 对非主线程 JNI 域的影响——从 ndk 主库 strings
   找线程相关开关，或调整 InitializeNativeBridge 的调用线程
3. 若新线程也成功：问题在壳 dex 类的解析域——从桥侧（Server.java）在 load 流程中
   打印类的实际来源与 ClassLoader

## 2026-09-27 四续：三大连环根因攻破，Guard 壳解密+真实 dex 加载打通

接手交接文档后按 §6.4 路线执行，四轮「改 artlaunch → scp → mk_x86_initrd →
mi_test → bench_guard.py」循环打穿三根因（详细论证见交接文档 §6.5）：

1. **签名坑**：实验块静默 = JNI_GetCreatedJavaVMs 误声明两参（真三参）。
   修复后新线程 native_get 成功 → **线程域假设排除**（文档旧嫌疑①被否）。
2. **注册时序**：ndk InitNB 触发 Build.<clinit> 时 boot natives 未注册 →
   Build 永久 erroneous → 壳链必死（旧卡点真身）。注册挪到 InitNB 前，
   Build.CPU_ABI=arm64-v8a（ndk 伪装层正常）。修饰符审计：LOS20 native_get(String)
   非 native 属正常；native_find_prop 已改名 native_find(String)J。
3. **namespace 判定**：System.load(arm64) 走 bionic dlopen——libnativeloader 的
   NativeLoaderNamespace::Create 靠 NativeBridgeIsPathSupported 定 bridged，
   ndk 对一切路径 false（含官方 /data/app/…/lib/arm64）。proppreload 接管该回调
   （/data/catclaw → true）。顺带把 LoadNativeBridge 挪到 InitializeNativeLoader 前。
4. **g_runtime_callbacks**：LoadNativeBridge 第二参（ART 的 9 函数 callbacks 表）
   传 NULL → 壳 so 加载时 ndk CHECK 失败 abort。反汇编 libart 定位静态全局
   art::native_bridge_art_callbacks_（未导出），按导出锚点求基址重建 9 指针表。

里程碑：load WoGGGuard → DexNative.<clinit>（Build.CPU_ABI 检查）→ fty so 转译
加载执行 → **壳解密出真实 dex 并加载成功**（壳日志「自定义爬虫代码加载成功」）。

新卡点（交接文档 §6.6）：壳日志后进程无 dump 静默退出。嫌疑：artlaunch 自实现的
sigchain 没做链式转发，转译段真实 SIGSEGV 被误杀；或壳 init 后段缺系统支撑。
下一步从信号链（g_chain 升级为真链）+ LOGD_HEX 取证入手。

诊断脚本沉淀：tools/x86guest/bench_guard.py（Guard 验收）、sym_lookup.py
（libart 符号→函数指针表还原，重建 callbacks 时用过）。

## 2026-09-27 晚：宿主实测复现 + 退出码/信号取证 + 非 Guard 源验收

宿主（Windows，CATCLAW_X86_GUEST=1）实测与 108 完全一致：桥就绪 → 玩偶 jar
取回 90ms → DexClassLoader 就绪 → 壳解密成功 → 39ms 后桥死。

取证三件套：
1. init_x86.sh 加 `wait` 打桥进程退出码 → **139 = SIGSEGV**；
2. artlaunch 的 sigchain 实现包信号哨兵 trampoline（write 打 signo/si_code/si_addr
   后转发真 handler）→ 崩溃前大量 SIGSEGV 正常流转（accurate-sigsegv 的 fault
   机制在工作），致命一次 `si_code=1 si_addr=0x0`——**空指针**；
3. 拉 BlissRoms-x86 官方集成 commit 与 prebuilt 仓库 mk：属性组与我们完全一致，
   无新配置——排除了「少设属性」假设。

定性：转译引擎 OK，死因 = 真实类初始化在极简环境拿到 NULL 依赖（闭源加固代码
不判空）。突破方向：补框架支撑面对齐完整系统。

非 Guard 源验收（bench_speed.py/bench_pick.py）：fty.jar（900+ 类，宿主
NonGuardFallbackJars 同源）——load 全通（0.06~0.17s）、homeContent/
categoryContent/detailContent 调用链全 ok、毫秒级响应。aarch64 TCG 的 71~90s
对照下，**x86 mini guest 的核心性能价值已实证**。

## 2026-09-27 深夜：宿主联调两个宿主侧事故修复

宿主实测又暴露两个问题，均已修复并验证（commit 437134a + Maui 工作副本）：

1. **多 qemu 并存（3~4 个）**：ResetBridge 的 Shutdown 在后台任务跑，置空 _art
   前的 Dispose 窗口期内，并发 EnsureBridgeAsync 拿旧引用 → 旧 QemuHostRuntime
   被 StartAsync「复活」（日志特征：同一媒体口 18601 反复出现）→ 随后又 new 新实例。
   修复四层：Shutdown 先 Interlocked 原子摘引再销毁；_bridgeEpoch 会话代际
   （重置同步自增，冷启动返回后校验，不一致换流重试）；QemuArtGuest._disposed
   禁复活（探针循环早退）；QemuHostRuntime StartAsync/Stop 生命周期锁 + x86 启动前
   按名回收孤儿。实测：重置后旧实例干净停止、无复活启动、x86 qemu 收敛为 0~1 个。
2. **探针连接无 pong（握死）**：信号哨兵 trampoline 每次 SIGSEGV 都 write 到
   console——宿主握手期 console 管道无人读，ndk 转译 fault 高频信号很快写满
   64KB 管道 → write 阻塞在信号处理里 → guest 全线程冻住（探针 accept 了但
   pong 永远不回，手工 ping 也超时）。修复：哨兵日志默认关（CATCLAW_SIGLOG=1
   才输出）；教训——信号处理内不得做无节流 stdio 写入。

## 2026-09-27 深夜（二）：迅雷引擎合并方案可行性验证 ✅

用户澄清：架构上迅雷引擎与 QemuArtGuest 应合并进同一个 x86 guest（架构图里
qemu-aarch64-static 那行就是预留方案），而不是各占一个 QEMU 实例。

**可行性探测（108 实机，qemu-user 直跑）**：
- 从宿主 QemuGuest/pkg_initrd.gz 解出迅雷件（harness 212KB + system/ 11MB）
- `qemu-aarch64-static -L <thunder目录> ./harness` ——**直接跑通**：
  bionic linker64 加载（ld.config 警告无害）✓ 迷你 JNIEnv（43 实现/190 陷阱）✓
  DNS 自检（UDP 114）✓ 引擎加载 ✓ 真调用——
  getDownloadLibVersion() = 6.0529.260.26、XYVodSDK_getVersion() = 2.0.8.15-arm64_v8a
- `-L` sysroot 天然解决 /system/bin/linker64 与 /system/lib64 绝对路径
- 件体积：qemu-aarch64-static 16.6MB static-pie + thunder 11MB —— initrd +28MB 可接受
- harness 参数全走环境变量（CTRL_PORT/BLK_DEV/PROXY_PORT/QCO/GUARD_PORT），
  cmdline 只需 ctrl=/blkdev=/swapdev=/tdata= —— x86 guest 1:1 可提供

**收益预估**（对照 qemu-engine-performance.md）：全系统 TCG 2.9%（19.5 MB/s）
→ 用户态转译 7.2%（约 2.5×，且跑在 WHPX guest 里少一层嵌套）；省掉整个
aarch64 VM（-m 2560 + 4 vCPU TCG 满负荷）。

**关键设计点（实施时注意）**：ART 桥重置当前 = Stop 整个 VM，会连带杀迅雷会话
——合并时应改「init 监督器：artlaunch 死了只重启它，VM 不动」。
实施清单见交接文档 §6.8。

## 2026-09-27 深夜（三）：迅雷控制端地址环境变量化（用户要求 localhost）

用户指出 harness 里硬编码的 `10.0.2.2:18080` 应为可配置的本机地址。改造：

- `ctrlloop.c`/`guard.c`：5 处 http 调用（/task 轮询、/report×2、/res×2）从字面量
  `"10.0.2.2"` 改为 `ctrl_host()`——读 `CTRL_HOST` 环境变量，**缺省 127.0.0.1**；
  guest 部署由 init 显式 `export CTRL_HOST="10.0.2.2"`（SLIRP 约定，行为零变化）。
- 至此三种场景统一：108 直跑/同机调试用默认 localhost；guest（现网 aarch64 VM、
  未来 x86 合并）用 init 注入的 10.0.2.2。
- 闭环验证（108 qemu-user）：假控制端 `thunder_probe.py` 监听 127.0.0.1:18080，
  `CTRL_HOST=127.0.0.1 ... ./harness` → /task 轮询抵达、PING→pong 上报回环 ✓
- 产品件重打（repack_pkg.sh：新 harness + init 注入 CTRL_HOST）→ hosttest 回归
  🎉 全链路通过（磁力→6 项文件列表→HTTP 206→起播，18.8s）。
- 观察记录：hosttest 默认磁力链路径偶发一次引擎线程 SIGSEGV（重跑不复现；产品
  运行时 `MAGNET=none` 不走该路径，无害）；排查中确认 108 直跑与 guest 原生两种
  环境的对照方法（同包同链 35s 对跑）。