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
ThunderRuntime/
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