# ThunderRuntime（QEMU 迅雷引擎运行时）

Windows 上承载 **ARM64 Android 迅雷下载引擎**的 QEMU 运行时，随应用分发到 `<app>/ThunderRuntime/`。
引擎从这里加载（`AppContext.BaseDirectory/ThunderRuntime`），宿主侧实现见
`CatClawVideo.Core/Services/QemuThunder/`，完整链路说明见 `JavaBridge/qemu-src/README.md`。

| 文件 | 说明 | sha256（前 16 位） |
|---|---|---|
| `qemu-system-aarch64.exe` | QEMU 11.1.0（v11.1.0-12130-ge470268ff4），win64 | `952f897511177803` |
| `pkg_kernel` | Linux 6.12.110-0-virt（aarch64，含 virtio-net） | `7c14372180c03e55` |
| `pkg_initrd.xz` | initrd = busybox + bionic 垫片 + harness + 迅雷 .so；由 `JavaBridge/qemu-src/build/build_initrd.sh` 构建 | `6b956c668302073e` |
| 其余 DLL | QEMU 依赖（**图形栈已裁剪**：libGLESv2×3 / libEGL×2 / libvk_swiftshader 共 6 个 DLL 已移除，`-nographic` 无头运行不需要；其余硬依赖经存活探测确认保留） | |

⚠️ **端口约定**：initrd 里烧死了控制口 `18080` 与代理口 `20080`（guest 以 `10.0.2.2` 回连宿主）；
宿主媒体口默认 `20092`（被占自动向后漂移）。改端口需重新构建 initrd，并同步
`QemuThunderEngine.CtrlPort / PreferredMediaPort`。

⚠️ **进程生命周期**：QEMU 只由应用按需启动（懒启动），空闲 15 分钟自停；子进程挂在
KillOnClose 的 Job Object 上，应用退出/崩溃不会留下孤儿 VM。
