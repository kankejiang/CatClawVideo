# 迅雷引擎（ARM64）在 x86 宿主上的性能：实测数据与方案取舍

> 本文回答三个反复被问到的问题：**为什么引擎这么慢？能不能换容器？能不能把 `.so` 反编译成 x86？**
> 全部结论都有本机实测数据支撑，并标注了测量方法，便于复现与反驳。

## 结论摘要

| 问题 | 结论 |
|---|---|
| 引擎为什么慢 | **跨架构软件模拟（QEMU TCG）**，与虚拟化、网络、vCPU 数都无关 |
| 换容器行不行 | **容器本身解决不了跨架构**（共享宿主内核）；但 Android 的 native bridge 可以，代价是专有库 + 收益仅约 2× |
| 反编译 `.so` 成 x86 | **不可行**（技术产物不可编译 + 违反 EULA/著作权） |
| 产品影响面 | 只有**磁力**过引擎；HTTP 直链不经引擎，重读走直读（4578 MB/s） |
| 是否值得改造 | **不值得**。现状 19.5 MB/s 对磁力场景够用，真·零损失方案是 ARM64 宿主 |

## 1. 引擎是什么

从 `ThunderRuntime/pkg_initrd.gz`（QEMU guest 的 initrd）中提取：

| 文件 | 体积 | 备注 |
|---|---|---|
| `system/lib64/libxl_thunder_sdk.so` | **5.35 MB** | 优化过的闭源 C++；`.symtab` 已剥，但 **`.dynsym` 保留 12984 个导出符号**（C++ 类名完整可读） |
| `system/lib64/libxl_stat.so` | 0.80 MB | 同上 |
| `harness` | 0.10 MB | 我们自己写的 ARM64 ELF，负责调引擎 |
| `system/lib64/lib{c,c++,m,z,log,android,dl,stdc++}.so` | 3.46 MB | bionic 运行库（Android 用户空间） |
| **合计** | **8.92 MB** | — |

架构为 **ARM64（AArch64）**。**迅雷不提供 x86_64 版本**——这是所有问题的根源。

运行时形态：`QEMU（宿主进程）→ 自建 ARM64 kernel + initrd（bionic 用户空间）→ harness → libxl_thunder_sdk.so`。
宿主是 Windows x86_64，**只能走 TCG（纯软件翻译）**：WHPX / Hyper-V / KVM / HVF 依赖 VT-x/EPT
等硬件虚拟化扩展，**而这些扩展只认 x86 指令，跨架构时它们一点忙都帮不上**。

## 2. 效率对照（本机实测）

### 2.1 跨架构模拟器对比 —— 同一段 ARM64 代码

样本：initrd 里的 **ARM64 静态 busybox**，执行 `sh` 里 200 万次算术循环。
对照组为 debian13 (x86_64) 容器上的原生 `sh` 执行同一循环。

| 环境 | 机制 | 耗时 | 相对原生 |
|---|---|---|---|
| x86_64 原生 | — | **1.72 s** | 100% |
| ARM64 经 `qemu-user`（用户态模拟，宿主 Linux） | 软件翻译（用户态） | **24.0 s** | **7.2%** |
| **ARM64 全系统 QEMU（TCG，现状）** | 软件翻译（含内核/设备） | **59.6 s** | **2.9%** |

⇒ 用户态模拟比全系统快 **2.5×**（省掉内核/设备/中断模拟），**但仍然是模拟**，没有数量级改变。

### 2.2 虚拟化损失 vs 模拟损失 —— 别把两者混为一谈

样本：同一段 Python CPU 循环（3×10⁷ 次整数运算），取三次最优。

| 层级 | 环境 | 机制 | 耗时 | 效率 |
|---|---|---|---|---|
| L0 | Proxmox 宿主（i5-13500T 物理机） | 原生 | **1.97 s** | 100% |
| L1 | 这台 Windows（Proxmox KVM guest） | **同架构硬件虚拟化** | **2.40 s** | **82%** |
| L2 | ARM64 guest（引擎所在） | **跨架构软件模拟** | 59.6 s（2.1 节同源样本） | **~3%** |

⇒ **虚拟化只损失 18%**（与"嵌套虚拟机损失 15~30%"的经验值一致）；
**90%+ 的损失全部来自"跨架构"**。看到"性能只有个位数百分比"时，先问：guest 与宿主**是不是同架构**？

### 2.3 引擎的真实速度与影响面

同一次运行内的三个参照物（同源，均指向同一 URL）：

| 项目 | 速度 |
|---|---|
| 宿主直连互联网 | **38.1 MB/s** |
| guest 内 `wget` 纯 TCP（经 SLIRP） | **40.8 MB/s** |
| **迅雷引擎** | **19.5 MB/s** |

- 纯 TCP 甚至快过宿主直连 ⇒ **SLIRP 与宽带都不是瓶颈**，**引擎才是**。
- 下载时 QEMU 占 **3.4 核 / 4 vCPU 的 85%** ⇒ **CPU 受限**。
- 与并发 pipe count 无关（4/16/35 都 11~12 MB/s）；**加核无效**（`smp` 4→8 反而 **19.5→15.3 MB/s**）。
- ⚠ 对比引擎速度**必须同源**：拿 A 家镜像的引擎数据比 B 家镜像的 wget 数据会得出完全错误的结论。

## 3. 为什么"容器"解决不了跨架构

**容器的本质是共享宿主内核** ⇒ 容器内程序的架构**必须与宿主内核一致**。
宿主 x86 → 容器里只能跑 x86 程序 → **ARM64 的 `.so` 连加载都做不到**。

Windows 上的"Linux 容器"更绕：

| 方案 | 真相 | 能否跑 ARM64 `.so` |
|---|---|---|
| Docker Desktop（Linux 模式） | 底层是 **WSL2 / Hyper-V 的 Linux VM** —— 它本身就是虚拟机 | 架构仍 x86 ❌ |
| Windows Container | 只能跑 Windows 程序 | 引擎是 Linux `.so` ❌ |
| WSL2 | Hyper-V 轻量 VM（不是容器） | 架构仍 x86 ❌ |

**"容器基本没有性能损失"这句话本身是对的 —— 但只在同架构时成立。**

## 4. 唯一能大幅提升 CPU 效率的现成方案：Android native bridge

Android 生态早就为跨架构造了一座专门的桥：

- **`libhoudini`**（Intel，Android 5.0 起）→ **`libndk_translation`**（Google，Android 11+）
- 机制：`binfmt_misc` 注册 + `/system/lib64/arm64/` 布局 + `ro.dalvik.vm.native.bridge=libndk_translation.so`
  ⇒ **x86_64 的 Android 用户空间可直接加载 ARM64 `.so`**
- 第三方实测：**Houdini 3D 游戏 85% 原生 / NDK Translation 72%**（另一来源给"20~40% 开销"）
- **容器化 Android（`redroid`）默认自带 native bridge**（README 列三种：libndk_translation 好 /
  libhoudini 极好 / QEMU translator 中）⇒ **"容器 + 翻译层"才是"容器方案"的正确形态**

### 为什么我们不采用

1. **收益只有 ~2×，不是数量级**：native bridge 把 CPU 效率从 2.9% 提到 ~70%（24×），
   但**引擎并非纯 CPU 受限** —— 它现在已打到带宽的一半（19.5 vs 40.8 MB/s），
   换上去顶多 **30~40 MB/s**。
2. **法律风险**：`libndk_translation` / `libhoudini` 都是**专有二进制**，
   包是"从 Google 官方模拟器镜像里 collect 出来"的（上游 README 明写 `collect from Android Emulator`），
   移植教程自己都标注 *"This might not be legal"*。**随产品分发有风险**，合法路径只能让用户自备。
3. **工程量大**：要把"ARM64 guest"整体换成"x86_64 Android guest"（Hyper-V/WHPX 加速 + 翻译层配置）。

## 5. 反编译 `.so` 成 x86：为什么不行

| 目标 | 可行性 |
|---|---|
| 反编译出**可读代码** | 可行（Ghidra / IDA / Binary Ninja / RetDec），但产物是**伪代码** |
| 伪代码**重新编译** | **不可行**。优化过的 C++（内联、循环展开、常量传播、模板、可能的内联 NEON 汇编）反编译后结构已与源码不同，人工修复成本高于重写 |
| **静态二进制翻译**（McSema / rev.ng 等） | **不适用**。面向固件、老游戏、小程序；对 5 MB 商业 C++ 引擎不成立 |
| 法律 | **明确踩线**。商业 EULA 普遍禁止逆向；反编译后重编分发 = 侵犯著作权 + 违反迅雷 SDK 许可 |

## 6. 现状速度的真实影响面（比"引擎慢"更重要的认知）

产品里**只有磁力链接会送给引擎**（`CatClawVideo.Core/Providers/CompositeVodSourceProvider.cs:106`）：

```csharp
if (!src.Episodes[i].Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
```

| 场景 | 路径 | 速度 | 可改性 |
|---|---|---|---|
| **磁力** | 必须过迅雷引擎（P2SP 是它唯一价值） | 19.5 MB/s（TCG 封顶） | ❌ 无替代 |
| **HTTP 直链**（MacCMS / Web / 插件解析出的 m3u8·mp4） | **不经引擎**，直接交播放器 | 宿主带宽 | ✅ 本来就绕开 |
| **重读 / seek / 多读者** | 块设备数据面**直读** | **4578 MB/s** | ✅ 已实现 |

数据面（稀疏块设备）语义要点：

- `blk_write` 只在**宿主读**时触发，**引擎下载不写块设备** → 必须**先回填一次**，之后同区间重读才走直读。
- 实测：HTTP 经 SLIRP **14.9~16.9 MB/s** vs 直读（热数据）**4578 MB/s** / 单次 256 KB **74 µs** ⇒ 约 **270×**。
- ⚠ 镜像复用会让数据变热（单次 256 KB 394 → 74 µs）→ **报数必须注明冷/热**。

## 7. 决策与后续

**现状不改造**，理由：

1. 引擎只影响磁力场景，19.5 MB/s 对 BT/P2SP 下载是可接受量级；
2. 首次落盘之后的**重读、seek、多播放器全部绕开引擎**（4578 MB/s）；
3. 唯一能提速的现成方案要背专有库的法律风险，且只换来 ~2×。

**若将来确要提速，按性价比排序：**

| 方案 | 预期 | 代价 |
|---|---|---|
| 提高数据面直读覆盖率（**推荐**） | 掩盖引擎低速，收益不受引擎限制 | 纯软件，无法律风险 |
| x86_64 Android + native bridge | ~2×（30~40 MB/s） | 重做架构 + 专有库法律风险 |
| ARM64 宿主（Apple Silicon / ARM 服务器 / ARM64 Linux） | **原生 100%** | 需要 ARM 硬件 |

**最容易被忽略的事实**：**绝大多数手机就是 ARM64**，所以**手机上引擎本来就是原生 100%**。
本文的 2.9% **只有 x86 Windows 桌面版和 x86 Android 设备在付** —— 这是桌面版独有的降级，不是产品的普遍问题。

## 附录 A：复现步骤

| 目的 | 命令 |
|---|---|
| 数据面直读基准（宿主侧，不需 QEMU） | `hosttest/bin/Release/net11.0/ThunderHostTest.exe xfer <镜像> <块大小> <次数>` |
| 端到端引擎基准（真实 QEMU） | `ThunderHostTest.exe xfer-e2e <ThunderRuntime 目录> <块大小> <次数> <控制口>` |
| qemu-user 对照 | 宿主 Linux 上 `qemu-aarch64-static <arm64 静态二进制> ...`（需先 `apt install qemu-user-static`） |

## 附录 B：测量注意事项（踩过的坑）

1. **同源对照**：引擎与 `wget` 必须指向同一个 URL / 同一镜像。不同源的数字不可比。
2. **冷热标注**：镜像复用后数据变热，同一操作可能快 3~5×。
3. **环境前缀**：本机 `WIN-58R5VVVBFMQ` **本身是 Proxmox 上的 QEMU 虚拟机**（i5-13500T 20 vCPU / 15.9 GiB，VT-x 已透传）。
   所有绝对数字都带"嵌套虚拟化"前缀：**物理机 → Proxmox KVM → Windows VM → QEMU TCG 模拟 ARM64**。
   **不能当作产品在普通 PC 上的表现**；相对比值仍然成立。
4. **`xfer-e2e` 需要控制口 18080 空闲** —— 即不能同时有 CatClawVideo 实例在运行。
5. **guest 内不能起 HTTP 服务**：initrd 的 busybox 没编 `httpd`/`dd`/`nc`（只有 `wget`/`tftp`/`sh`）；
   且该 busybox **不支持 HTTPS、只认 HTTP 200**（对无 Range 请求回 206 会直接放弃）。
   另：它只认 `argv[0]` 的 basename 恰为 `busybox` 时才走 applet 分发，**改名会一律报 `applet not found`**。
