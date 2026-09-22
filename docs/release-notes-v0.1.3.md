猫爪影视 v0.1.3 —— 本次更新以 **「开箱即用」与「播放/下载稳定性」** 为主线：Windows 端补齐随包运行时（不再需要自备 Java，也不需要另装 VC++ 运行库），把「播放到一半自己退出」「下载大文件中途中断」这两类问题从根上修掉，并显著降低磁力播放的内存占用、提升读盘性能。

> 自 v0.1.2（2026-09-19）以来共 **52 个提交**，跨度 09-19 ~ 09-22。

---

## 一、开箱即用：随包运行时

### 1. 随包精简 Java 运行时（新增）

**问题**：Windows 端 jar 类爬虫源要求用户自备 JDK，而包里没带 —— 桥是随包的（`JavaBridge`），但 `java.exe` 不随包（只在 `JAVA_HOME` / `Program Files` / `PATH` 里找）。找不到就判定该源不可用，界面显示「爬虫源 · 需 spider 运行时」，而**这句提示完全没说要装什么**，用户只能来问。

> Android 不受影响：那边走系统 ART 的 `DexClassLoader`，不需要外置 JVM。

- `JavaBridge/jre/` —— 用 jlink 从 **Microsoft OpenJDK 21**（MIT 许可，可再分发）裁出的精简运行时：**62 MB / 201 个文件**，`java -version` = 21.0.11。模块集按桥的实际需要挑，宁多勿少（`java.base/desktop/net.http/scripting/sql`、`jdk.unsupported/charsets/localedata/zipfs/httpserver`、`jdk.crypto.ec/cryptoki`、`java.security.jgss/sasl` 等）。
- `FindJavaExe()` **优先返回随包运行时并短路** —— 这不只是"顺手优先"：`bridge.jar` 的字节码是 **major 65（Java 21）**，若按原来的"版本号最大"去挑系统 Java，用户装了 Java 17 就会被挑中而报 `UnsupportedClassVersionError`。随包那份版本永远正确，因此直接返回、不与系统 Java 比大小。

### 2. 安装时自动补 VC++ 运行库

**问题**：随包的 8 个原生 DLL 依赖 VC++ 2015-2022 运行库，而产物里 **app-local 一个都没有**、安装脚本也没有任何处理 ⇒ 干净的 Windows 上 FFmpeg 加载失败、**视频根本播不了**（开发机因为 `System32` 早就装过而不暴露）。

```
FFmpegInteropX.dll                  → CONCRT140 / MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
avcodec-62 / avformat-62 / avutil-60
  / swscale-9 / swresample-6 / avdevice-62 → VCRUNTIME140
avfilter-11                         → MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1
```

- `installer/vc_redist.x64.exe` —— 微软官方原件（`aka.ms/vs/17/release/vc_redist.x64.exe`，14.44.35211.0，25,635,768 B）。**特意说明**：VS Package Cache 里那份同名文件只有 615 KB，是**下载器桩**（不带 MSI/cab 载荷），离线装不上，不能用。
- 安装脚本：`[Files]` 解到 `{tmp}` 且 `deleteafterinstall`（不往用户机器留垃圾）；`[Run]` 带 `Check: NeedsVCRedist`（缺才跑）。
- `VCRedistPresent()` 读注册表判定「已装」：`Installed=1` 且 `Minor>=29`（VS2019 / 14.29 起才有 `VCRUNTIME140_1.dll`，能把只装过 VS2015 的机器也正确归为"需要装"）。
  - ★ 不直接测 `VCRUNTIME140_1.dll` 是否存在的原因：实测该文件**只在 `System32`、不在 `SysWOW64`**，而安装程序是 32 位进程 —— 查 `System32` 会被 WOW64 重定向到 `SysWOW64`，得出**误判缺失**。

### 3. 新增 `docs/user-runtime-requirements.md`

两个平台"用户到底要装什么"逐项列清（含实测依据）。本次两项修完，该文档里标为待修的项目已清空。

## 二、播放中断：三个根因

### 1. 响应体短读（主因）

`ServeAsync` 先声明 `Content-Length: {length}`，却在等待 60s 到点后 `break` **截断响应体** —— 实际送出的字节少于声明长度。FFmpeg 的 http 层把短读当成 EOF → `MediaEnded` → 播放"结束"退出，而且**不可恢复**（播放器以为文件就这么长）。

- 越大的文件越容易命中：seek / 断点续播到未下载区时，引擎要按需下载该区间，文件越大越慢。
- **修法**：到点只**自救**（打断上游、让它重发 `Range`），连接保持继续等 —— 播放器自己的读超时会断开重连、重发 `Range`，等于一次**可恢复的重试**。另加 5 分钟绝对上限，并每秒探测播放器是否已关连接（避免残留请求压住 `_requests` 最小位置、害上游从旧位置白拉整段）。

### 2. `BodyStallMs` 是个声明了却从未接线的死常量

上游 body 停滞时旧代码无限 `continue`，引擎"TCP 不断但不再供数"就会一直挂着，下游只能烧完预算后走上面那条短路。

- **修法**：接线 —— 已收到过字节（连接已过期）按 **8s** 判；一个字节都没收到（引擎还在下该区间）放宽到 **48s**，到点断开让外层重发 `Range`（换连接本身会重新触发引擎按需供数）。

### 3. 大对象堆 churn

整块落盘的 4 MB 快照、按需预取的 4 MB 缓冲原本"每流 4 MB 就新分配 4 MB"（必然进 LOH，且默认不压缩）⇒ 边播边下时托管内存只涨不落、GC 越来越长（进而吃掉上面的等待预算）。两处改 `ArrayPool` 池租，使用点均走显式长度。

## 三、下载与存储稳定性

- **tmpfs 1500m → 3500m**：消除「下载超过约 1.57 GB 必死（`err=114010`）」。顺带发现：仓库里那个能改 `/init` 的 `repack_initrd.cs` **提交后从未真正跑过**（注释写着 1500m 会死，实际 initrd 还是 1500m）—— 本次用它重打包并逐项核对（`/init` 92 行、`/harness` / `busybox` / `setting.cfg` 字节数新旧完全一致，只有尺寸变了）。
- **块设备容量 16 GB → 64 GB**：`IsRangeAvailable` 里 `offset + count > CapacityBytes` 直接返回 false ⇒ 超过 16 GB 的部分**永远命不中数据面直读**，只能退回 HTTP（**4578 MB/s → 40 / 18.9 MB/s**）。20~60 GB 的 4K remux 恰好被挤出快路径 —— 越大的片越慢、越容易烧完播放端等待预算。稀疏镜像调大不占盘。
  - ⚠️ **一次性代价**：已存在的 16 GB 镜像因大小不匹配会被重建，块设备里已缓存的数据丢一次（宿主磁盘缓存不受影响）。
- **磁力任务单并发闸**：迅雷引擎是**单 VM 单会话**，新任务会顶掉旧会话 ⇒ 两个磁力同时跑必然互顶（用户实测：暂停后恢复会提示「引擎被占用」）。现在磁力**永远串行**，与并发设置无关。
- **句柄重建退避加长**：`stopTask` 后重建的退避从 0.5/1.5/3/3s×4 改为 1/2/4/8/8s×5（≈23s），并在错误码 ≠9128 时**立即停止重试**（换了别的错说明重试也救不回来）。实测原退避 4 次全部仍撞 9128。
- **`AllocatedBytes` 在碎片镜像上静默报 0**：`FSCTL` 单次最多回 64 个区间，超出就丢 —— 已修。
- initrd 重打包工具支持块设备并保证幂等。

## 四、内存与性能

### 1. 挂交换区块设备：`-m 5120` → `2560`

给 guest 挂一块稀疏 swap 镜像（不需要文件系统，签名由 guest 的 `mkswap` 写），**内存与文件大小彻底解耦**：

| | 改前 | 改后 |
|---|---|---|
| guest RAM | ≈1.5 GB **+ 已下载量**（≤3.5 GB） | **恒定 ≈2.5 GB** |
| tmpfs 上限 | 3500m | **6144m** |

- 顺带修掉一个隐藏 bug：>3.5 GB 的片不再被 `err=114010` 打死。
- 新增四个可调参数 `GuestMemoryMb(5120)` / `GuestMemoryMbWithSwap(2560)` / `DataDirMb(3500)` / `DataDirMbWithSwap(6144)`；**swap 不可用时四个值全部退回旧行为**。
- 设备顺序决定 `vda`/`vdb`，而数据面孔是**可选**的 ⇒ 设备名由宿主认定后经 cmdline 传下去（`blkdev=` / `swapdev=`），**不让 guest 猜** —— 猜错会把引擎字节按文件偏移写进交换区。
- 实测降低 `-m` 另带来约 **1.5×** 提速。

### 2. TCG 调参：**1.22×**

- `-cpu max` → `cortex-a76`：跑分中位 67s → 55s（4 轮采样分布不重叠）。`-cpu max` 会打开 SVE/SME，TCG 下其状态保存与翻译块占用更重；a76 虽无 SVE，但具备该引擎需要的 NEON / AES / PMULL / SHA1 / SHA2 / CRC32 / LSE atomics / dotprod（guest `/proc/cpuinfo` 实证）。
- 新增 `-accel tcg,tb-size=256,split-wx=off`：`tb-size` **不是越大越好**（512 实测 0.86×、64 实测 0.79×，256 最佳）；`split-wx=off` 有意关闭 W^X 双映射换性能。
- `smp` 由写死 4 改为按宿主核数但**封顶 4**：引擎吞吐峰值在 2~4 vCPU，本机 12 逻辑核（6 物理核）实测 `-smp 12` 反而 **0.78×**。
- 多实例实测（2~3 个 guest 同时下载）合计吞吐仅 1.28×~1.40×，且把每实例从 4 降到 2 会让合计从 38.1 掉到 29.1 MB/s ⇒ 限制每实例的是它**自己**的 vCPU 数，多实例交给 OS 调度即可。

### 3. 数据面改走稀疏块设备：**18.9 → 2454 MB/s**

起因是用户问「宿主与 VM 互传怎么会只有这么点速度」。实测定位（同机、产品同款配置，各 512 MB）：

```
guest 内环 TCP（数据不出 guest）    432.1 MB/s   ← 证明 ARM 模拟不是瓶颈
SLIRP 上行（guest→宿主）             40.0 MB/s   ← 掉 10.8 倍
harness 转发后（16KB select 循环）  18.9 MB/s   ← 再砍一半
宿主直读同一物理文件               2926   MB/s
virtio-serial 单通道                132~185 MB/s
virtio-blk + qcow2                   84.9 MB/s
```

⇒ **瓶颈是 QEMU 的 SLIRP 用户态 TCP 栈，不是 TCG 指令模拟**。顺带排除两条路：

- **virtfs / 9p**：官方 Windows 版 QEMU **全局禁用**（`-fsdev "support is disabled"`），不是裁剪问题，guest 侧 kernel 编了也白搭。
- **virtio-blk + qcow2**：COW 原生且实测 84.9 MB/s，但 guest 挂上 ext4 后 .NET 读不了文件内容（没有 ext4 解析），宿主直读废掉 —— 那是给"整块盘交给 guest"的场景。

**本方案（virtio-blk + raw 稀疏文件）**：guest 侧 harness 在转发的同时，把引擎吐出的字节按文件偏移 `tee` 进 `/dev/vda` —— 镜像内「偏移 N」就等于影片「偏移 N」（1:1），宿主无需文件系统、无需分配表；镜像建成稀疏文件（Windows 先 `FSCTL_SET_SPARSE` 再 `SetLength`），没写过的区间不占盘。实测逻辑 4 GB / 实占 0 MB → 写 512 MB 后实占 512 MB。

### 4. 其他

- **vCPU 数可配置**（`SmpCount`，默认仍 4）—— 供后续按机器调优。
- 控制服务器改绑 `0.0.0.0`（用户态模式无 SLIRP，guest 需直连 `10.0.2.2`）。

## 五、界面精简

### 1. 移除下载入口（三处，全部删除而非隐藏）

- 底部导航「下载」tab —— `MainPage` 是自绘导航（`Shell.TabBar` 关闭），索引在 4 处耦合，已全部对齐：`MainPage.xaml` 删掉 `NavBg3` 区块并把 `NavBg4/5` 重编号为 `NavBg3/4`（标签：本地 / 设置）、`MainPage.xaml.cs` 的 4 个数组与 `TabIndexOf`、`MainViewModel.Tabs`、`MainPage` 构造参数（6 项 → 5 项）、`MauiProgram` 去掉 `DownloadsPage` 的 DI 注册。自检：XAML 的 `NavBg`/`NavLabel` = 5、`Tabs` = 5、`_tabs` = 5，`NavBg5`/`NavLabel5` 零残留。
- `DownloadsPage` 右上角「＋ 新建下载」按钮（支持直链/磁力）。
- 观看页磁力行卡片下方的「⬇ 下载」chip。

> **保留**（成为不可达的休眠代码，本次未删）：`DownloadsPage` / `DownloadDetailPage` / `DownloadsViewModel` / `DownloadStatusConverter` / `DownloadManager` 与迅雷引擎链路 —— 本次只摘「入口」。若要彻底下线整条下载链路，需连同这些文件与任务恢复逻辑一起评估。

### 2. 移除观看页磁力行卡片下的「▶ 播放」chip

与「点卡片即播放」重复。

### 3. 设置页侧栏标题竖排（窄屏）

现象：侧栏「内容源」显示成一字一行的竖排，焦点环被撑成长方形。根因：`FocusableRow` 固定 6 列 + `ColumnSpacing=14`，5 个间距吃掉 70dp，而侧栏项只用得到图标与标题 —— 手机横屏逻辑宽约 411dp 时侧栏只剩几 dp。修法：新增 `RailOnly` 模式只建 3 列（焦点竖条 / 图标 / 标题），间距 14 → 10，标题加 `TailTruncation` + `MaxLines=1` 防换行；内容行保持完整 6 列不变。已实机验证（Android 横屏 + Windows 窄窗 520 宽）。

### 4. 源配置页 jar 站点误报「需 spider 运行时」

诊断日志显示 jar 桥实际可用（`jar桥=True`），界面却给 7 行站点挂着「需 spider 运行时」。根因：解析期 `StatusNote` 是按「是否 MacCMS(type=1)」判可播写入的，`type=3` 一律带这句备注；而界面只在 Script 分支做了「运行时可用就豁免」的判定，**Jar 分支漏了**。改法：豁免按 `SpiderKind` 分支（`Script→JsSpiderAvailable`、`Jar→JarSpiderAvailable`），运行时真不可用时提示照旧保留。验证：UIA 前后对比，命中该提示的元素 **46 → 0 行**。

## 六、Android

### 搜索闪退（Guard 壳 ART abort）

用户反馈「手机搜索视频时会闪退」。原生层 JNI abort，**C# 完全拦不住**：

```
signal 6 (SIGABRT) / tid: Thread-26
Abort message: JNI DETECTED ERROR IN APPLICATION:
  can't call java.lang.Class java.lang.ClassLoader.loadClass(java.lang.String) on null object
  from com.github.catvod.spider.DexNative.getSpider(...)
栈内：Init.getSpider → BaseSpiderGuard.<init> / AppgzGuard.<init> / LiveGzGuard.<init>
```

**为什么是"偶尔"**：搜索是全应用唯一会**并发遍历所有站点**的入口（`SearchPage` 里 `sites.Select(...)` 一次性把全部可播站点打出去，无任何并发上限）。平时其它页面只初始化一两个站点，撞不上；一搜索就是几十个并发。

**根因**：初始化并发**无锁** —— `EnsureSpiderAsync` 从 `TryGetValue` 到发布 `_spiders[key]` 之间没有任何同步，中间还夹着 `await` 与 `Task.Run`；而 `BindProtectedJar` 会往 jar 内**共享的** `Init` 静态单例写 `Context` / `DexClassLoader` 字段，Guard 壳的构造函数又要读这个单例取真实实现。搜索并发下同一站点被重复初始化；更致命的是**多个共用同一 jar 的站点**会同时写同一个单例 → 壳构造期读到 `null` → ART abort。

**修法**：新增 `_initGates`（站点级单飞）+ `_jarGates`（jar 级串行），把 `BindProtectedJar + NewInstance` 整段按 jar 串起来。对照实现本就都有锁（桌面桥 `synchronized(LOCK)`、JS 运行时 `GetOrAdd` + 双重检查），只有这里是漏网的。

顺带修掉「0 字节 jar 永久失效」。

## 七、Windows 安装版修复

### 1. 安装包补齐 Guard 解壳器，恢复全部片源

**问题**（用户安装版截图）：首页顶部报「🥝荐片┃多线 不可用 · 该站的 spider jar 是 Guard 加固包，unidbg 解壳失败且未找到替代的非 Guard 同族 jar」。

**根因**：csproj 原先**有意**只打包 `bridge.jar` + `vendor/deps`（约 3.4 MB），把 Guard 解壳用的 `vendor/dex2jar` + `vendor/unidbg`（约 **52 MB**）排除在外，理由是体积。于是安装版缺少解壳能力：Guard 站点只能退回非 Guard 同族 jar，而**没有替代品的站点**（荐片|多线、有声|小说、急救|教学）直接不可用 —— **安装版片源比开发版少一批**。兜底逻辑本身是对的（`FindBridgeDir` 的 Score 会让"能力更全"的仓库副本胜出），所以开发机跑仓库目录时从不暴露；只有安装包（只读、且只带 `deps`）才触发。

**修法**：csproj 的 windows 分组补两条 `Content`，把 `vendor/dex2jar/*.jar` 与 `vendor/unidbg/*.jar` 一并 Link 进 `JavaBridge/`（两者内容全是 jar，原生库都内嵌在各自 jar 的 `natives` 目录里，`*.jar` 通配符即可覆盖）。**已验证**：`JavaBridge` 目录由 3.1 MB 变为 **55.29 MB**，启动日志变为 `Guard 加固包 → unidbg 解壳`。

### 2. 安装版 JavaBridge 写程序目录被拒

装到 `Program Files` 后，桥要往程序目录写临时文件会被拒（该目录不可写）—— 已修。

### 3. 安装包体积：**251.3 MB → 238.0 MB（-5.3%）**

- 先**逐值实测**压缩档：`lzma2/ultra64`（当前）已是 Inno 6 上限，且**不支持自定义字典大小**（`lzma2/ultra64/512`、`lzma2/ultra/512` 等一律被拒）⇒ **参数上已无余量**，于是改从「少装」入手。
- 采用 A/B 实验量收益（同一 publish 目录三次压缩对比）：

| 版本 | 体积 | 相对基线 |
|---|---|---|
| 基线（全量） | 251.3 MB | — |
| **只排 AI 簇 + `*.lib`** | **238.0 MB** | ★ **省 13.3 MB（5.3%）** |
| 再排 QEMU 的 GTK/AV1/JXL | 230.2 MB | 累计省 21.2 MB |

- **采纳**（合计 43.3 MB 原始）：`*.lib`（9 个 / 1.4 MB，链接期文件）+ `onnxruntime.dll`(20.7) + `DirectML.dll`(17.8) + `Microsoft.ML.OnnxRuntime.dll`(0.8) + `Microsoft.Windows.AI.*`（22 个 / 4.0）—— Windows App SDK 的 Windows AI 组件随包带入，而本 App **代码里零引用**（`grep onnx|DirectML|Windows.AI` 无命中，csproj 也没有对应 `PackageReference`）。
- **不采纳**：再排 `ThunderRuntime/{libgtk-3-0, libaom, libSvtAv1Enc-4, libjxl}`（28.9 MB 原始）只再多省 **7.9 MB**，而它们出现在 QEMU 的**导入表**里、被加载期硬要求（Windows 动态链接在加载阶段就要求所有导入件在位）⇒ 收益不抵风险。

## 八、内部研究（不影响使用）

本轮有相当一部分提交是引擎行为探测与方案取舍的记录（`docs/qemu-*.md`、`docs/engine-network-interfaces.md` 等），例如引擎网络接口清单与抓包、TCG 各档参数矩阵、数据面各通道基准、以及若干条被实验证伪的加速路线。这些不改变用户可见行为，仅作技术档案留存。

## 提交清单（v0.1.2 → v0.1.3 全部 52 个）

```
fc183fe perf(installer): 排除 Windows AI 组件簇 + 链接期 .lib —— 安装包 251.3MB → 238.0MB（-5.3%）
8ee6037 fix(installer): 安装时自动补 VC++ 运行库 —— 补齐「开箱即用」最后一块
ee94f68 fix(gitignore): 否定 JavaBridge/jre/bin/ —— 否则随包 JRE 缺启动器（clone 后整套失效）
271e33f feat(jarbridge): 随包精简 Java 运行时 —— jar 类源开箱即用，用户不再需要装 Java
b96dc98 feat(qemu): 宿主为 guest 挂交换区块设备，-m 5120 → 2560（内存与文件大小解耦）
f888365 feat(qemu): /init 支持宿主经 cmdline 传参并启用交换区（工具 + 脚本 + initrd）
b0b243e refactor(ui): 移除观看页磁力行卡片下的「▶ 播放」chip（与点卡片即播放重复）
2f1636b refactor(ui): 移除下载入口（导航 tab / 新建下载按钮 / 观看页下载 chip）
7131f7d docs: 新增「下载/播放中断」根因与修法（含内存与中断的关联）
131590d fix(qemu): QEMU 进程死亡 1s 内感知（不再空转 180 分钟）+ 块设备容量覆盖到 4K 原盘
5ebe654 fix(qemu): 播放等待超时不再截断响应体（播放中断根因）+ 停滞重连 + LOH 池化
52a6624 docs(qemu): 新增 §8 —— CPU 亲和性实测负收益（0.66~0.78x），并标注批次间绝对值不可比
c56f64b docs(qemu): 复现路径同步到 _qemu_bench（反编译产物已清理），并补引擎吞吐/多实例基准的复现步骤
6a7012d perf(qemu): TCG 调参落地 —— 1.22×，smp 按宿主核数但封顶 4
e710147 docs: 部署约束定盘——零安装前提⇒全系统 QEMU 即最优架构；Windows 用户态模拟官方永不支持
c9626df qemu-user: ctrlserver 改绑 0.0.0.0（用户态无 SLIRP，guest 直连 10.0.2.2 所需）
1a58fb3 docs(engine): §13.8 终局否决 —— NAS 引擎登录需邀请码，该路线关闭
a771062 Merge branch 'master' of https://github.com/kankejiang/CatClawVideo
6b64435 fix(qemu/下载): 磁力任务单并发闸 + 句柄重建退避加长
618fd5d feat(qemu): ctrlloop 加请求头探针 —— dump HttpResource::GetHttpHeaderProperty
75dc468 docs(engine): §14 否定结论 —— OEM 解包拿不到「免登录 + x86 原生 + BT」
57152af docs(engine): §13.7 已把现代迅雷 NAS 引擎跑通，但任务 API 全部强制 JWT 鉴权
a65eb89 docs(engine): §13.6 更正 —— 登录是面板/云盘门槛，不是 BT 引擎门槛
8a78ae4 docs(engine): §13 磁力的正解 —— 现代迅雷引擎 xunlei-pan-cli 3.23.5 自带完整 BT 与边下边播
db8a01b docs(engine): 新增 §12 —— 迅雷 Windows 原生 x86 SDK 实测 112 MB/s（§11 否定结论的重要补充）
c0256c2 docs(tvbox): 更新参考源码路径为新下载位置（release 20260914-1520 / sourceCode-ab11d28）
87f1356 docs(qemu): 方案 B 证伪 —— 引擎给的直链带 rkey，外部回放一律 invalid rkey
514b2dc feat(qemu): 取到引擎的完整 HTTP 加速源（方案 B 最后一跳打通）
2d3f7f3 feat(qemu): vtable 扫描定位 ResourceManager，实测确认资源为 HttpResource
00756d8 feat(qemu): URLINFO 资源探针（fork 隔离未知签名）+ 控制服务器改时间片下发
2399233 feat(qemu): 加 PROBE 探针命令 + 宿主控制服务器脚本（方案 B 第一步）
41c6ce3 docs(qemu): 补上 harness 交叉编译链（debian 容器 10.0.0.108 的 NDK r27c）
94f3f01 docs(qemu): 混血方案路线图（VM 只做鉴权/索引 + 数据面宿主原生）
c468357 docs(qemu): 增 §8 各方案下载速度排序（含本机天花板与条件分支）
1f4a037 fix(qemu): 长跑抓包修正 —— 资源池 11400 通道 + .so 未 strip
6df29c5 feat(qemu): 抓包工具 + 引擎网络接口清单（filter-dump 实测）
1813794 docs(qemu): 引擎性能实测与方案取舍（容器/反编译/效率对照）
8347c57 feat(qemu): vCPU 数做成可配置（SmpCount，默认仍 4）
d05a649 test(bench): xfer-e2e 支持外部直链（用真实系统镜像考核引擎）
2915ea8 fix(qemu): initrd 的 tmpfs 从 1500m 提到 3500m（消除 >1.57GB 下载必死的 err=114010）
0f81f24 test(bench): xfer-e2e 的源改为可落盘的磁盘文件，并测「边读边导出」
0444f1e test(bench): xfer-e2e 补两个参照系 —— 宿主自测 + 纯 TCP 对照
df35129 fix(qemu): AllocatedBytes 在碎片镜像上静默报 0（FSCTL 单次最多回 64 个区间）
3384df9 test(bench): 数据面基准 —— 稀疏块设备通道的速度/延迟/并发（宿主侧 + 端到端）
00005f0 fix(ui): 源配置页 jar 站点不再误报「需 spider 运行时」
e30af6f fix(qemu): initrd 重打包工具支持块设备并保证幂等
58954b4 feat(qemu): 数据面改走稀疏块设备，绕开 SLIRP 瓶颈（18.9 → 2454 MB/s）
4790322 fix(win): 安装包补齐 Guard 解壳器（vendor/dex2jar + vendor/unidbg），恢复全部片源
7878d94 fix(android): 修掉搜索闪退（Guard 壳 ART abort）与 0 字节 jar 永久失效
7820e12 fix(win): 安装版 JavaBridge 写程序目录被拒（Program Files 不可写）
bef12ac fix(ui): 设置页侧栏标题竖排（窄屏侧栏只剩几 dp 宽）
1cb7f69 docs(release): 补全 v0.1.2 更新日志（含磁力/缓冲/搜索版面/设置页等全部改动）
```

## 产物说明

| 文件 | 平台 | 说明 |
|---|---|---|
| `catclaw.video-0.1.3-Setup.exe` | Windows | 安装版（self-contained，无需另装 .NET 运行时） |
| `com.catclaw.video-Signed.apk` | Android | arm64-v8a 签名包，Android 12 (API 31) 及以上 |

> Windows 端的磁力播放需要随包的 `ThunderRuntime/`（QEMU + 内核 + initrd），安装包已包含，无需额外下载。
> v0.1.3 起安装包**自带 Java 运行时**，jar 类爬虫源开箱即用；安装过程会在需要时自动补装 VC++ 运行库。
> **磁力播放依赖第三方迅雷引擎的可用性**；若某源起播失败，可在同剧的其他可播站点间自动换源。

## 相关链接
- 仓库：https://github.com/kankejiang/CatClawVideo
- 猫爪音乐（姊妹项目）：https://github.com/kankejiang/CatClawMusic
- QQ 交流群：855383639（可通过 QQ 搜索群号加入）

本项目仅为播放器壳，不内置任何片源，也不提供、不存储、不上传任何影视内容；所有源均由用户自行配置并对所添加的源及观看内容承担全部责任。
