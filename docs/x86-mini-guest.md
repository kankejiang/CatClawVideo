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