# 下载 / 播放大文件时「中断」的根因与修法

> 排查日期：2026-09-22　方法：链路代码逐处核实（行号均为改动前状态，改动见文末「已修」）
> 触发场景：**大文件**（4K remux / 长剧集包）下载到中途「停住不动」，或播放中途直接退出／回到列表。

---

## 0. 结论摘要

「中断」不是网络问题，是 **6 个具体缺陷**，其中两条与**文件大小正相关**（这就是「大文件才犯」的原因）：

| # | 缺陷 | 后果 | 位置 |
|---|---|---|---|
| **P1** | **响应体短读**：先声明 `Content-Length` 却提前 `break` | 播放「结束」/自动退出，**不可恢复** | `QemuStreamProxy.cs:838-840` + `:888` |
| **P2** | `BodyStallMs` 是**从未接线**的死常量 | 上游停滞时无限挂住，把 P1 逼出来 | `QemuStreamProxy.cs:25` + `:675` |
| **P3** | 看门狗只在 `ReaderStarving` 时判 | 播放器放弃后断粮永不被发现 | `QemuThunderEngine.cs:1200` |
| **P4** | 块设备容量写死 **16GB** | >16GB 的片静默退化纯 HTTP（4578 → 40 MB/s） | `QemuThunderEngine.cs:127` |
| **D1** | **QEMU 死亡无人知**：`Exited` 只打日志，下载轮询不看进程 | 进度冻结后**空转到 180 分钟**才报超时 | `QemuHostRuntime.cs:189` + `QemuThunderEngine.cs:370` |
| **D2** | 重启后任务只降级 `Paused`，不自动续跑 | 大文件跑到 80% 崩掉后需手动逐个继续 | `DownloadManager.cs:958` |

★ **内存持续增长与中断是同一件事的两面**，见 §2。

---

## 1. 链路（先认清在哪一层断）

```
播放器（FFmpeg / WinUI MediaPlayer / ExoPlayer）
  │  HTTP GET http://127.0.0.1:<随机端口>/s        ← Range 由代理自己实现
  ▼
QemuStreamProxy（本地读前缓存代理；QemuStreamProxy.cs:151/155）
  │  单条上游长连接，32MB 切片顺序续拉（:539）
  ▼
QEMU hostfwd  →  guest harness  →  迅雷引擎

取数优先级（:866-873）：块设备直读(_blockStore，4578 MB/s) > 内存窗 ReadAt > 磁盘 ReadDisk
```

代理对播放器**统一回 206 + `Content-Range`**（`:838`）—— 这是刻意的：若首个响应是 200，
FFmpeg 对远端 Cues 的 seek 会退化成「Soft-seeking by draining 1.9GB」，永远开不了播。

---

## 2. ★ 内存为什么和中断强相关

### 2.1 机制①（GB 级，主导）：guest 的「下载缓存」就是内存本身

`JavaBridge/qemu-src/src/patch_init.py` 第 1 行写明 harness 是**当作 initramfs 的 `/init`** 运行的
（`:18-23` 只 `mkdir("/thunder-data")`，**没有 `mount` tmpfs**）—— 因为
**initramfs 的根文件系统本身就是 RAM**，建在它上面的目录天然在内存里。

⇒ **下载多少就吃多少内存**：QEMU 进程 RSS = 固定开销（≈1.5GB）+ 已下载数据，上限 `-m 5120`。
这正是 `QemuHostRuntime.cs:142` 与 `docs/qemu-tcg-tuning.md:279` 说「`-m 5120` 是为了容下
`/thunder-data` 的 3500m」的真实原因。

**后果链**：

| 平台 | 发生什么 |
|---|---|
| **Android** | 内存到 GB 级 → **LMK/系统杀掉 QEMU → guest 静默消失 → 宿主失明（D1）→ 空转到 180min** |
| **Windows** | RAM 盘写满 → 引擎写盘失败 → 任务死亡（`err=114010` 一类）→ 进度冻结 |

★ **降内存还能提速**：`qemu-tcg-tuning.md:265-272` 实测 `-m 2048` vs `-m 5120` =
**29.8 vs 17.6~22.2 MB/s（约 1.5×）** —— 比 TCG 调参的 1.22× 更大。
（该文档自己标注此组混入 ±21% 噪声，**需交叉轮转 A/B 复测后才能改生产值**。）

### 2.2 机制②（百 MB 级，叠加）：托管堆 LOH 只涨不落

`ArrayPool` 原为 **0 处**。热路径上「每流 4MB 就新分配 4MB」，而 >85KB 必然进
**大对象堆（LOH，默认不压缩）** ⇒ 碎片化、阶梯上涨不回落 ⇒ GC 变长 ⇒
**吃掉 P1 的 60s 等待预算** ⇒ 更容易触发响应体短读。

| 位置 | 每次分配 | 频率 | 是否已修 |
|---|---|---|---|
| `QemuStreamProxy.cs:318` `FlushDiskChunk` | **4 MB** | 每写 4MB 磁盘缓存 | ✅ 已池化 |
| `QemuStreamProxy.cs:401` `PrefetchChunkAsync` | **≤4 MB** | 每预取一块 | ✅ 已池化 |
| `QemuStreamProxy.cs:195` `Append` 内 `new byte[count]` | ≤256 KB | **每次 `sock.Receive`** | ❌ **待办**（见 §5） |
| `:627 / :646 / :657 / :851` | 256 KB | 每切片 / 每请求 | 量级可忽略 |

### 2.3 已排除（**不是**泄漏，别再往这查）

- `_streamProxy` 的 3 处置空（`QemuThunderEngine.cs:645 / 685 / 805`）**都同时调了 `Dispose()`**
- 块设备镜像是**磁盘稀疏文件**（`SparseBlockStore.cs:22/92`，`FSCTL_SET_SPARSE` + `SetLength`），不占内存

---

## 3. 日志速查：一眼判断命中哪一条

| 日志关键字 | 命中 |
|---|---|
| `[proxy] 等待数据 60s…→ 自救：打断上游重发 Range，不截断响应体` | **P1**（原为 `播放器请求等待数据超时（60s）`） |
| `[proxy] 上游 body 停滞 Ns（本次已收 xKB）→ 断开重发 Range` | **P2** |
| `[qemu] 进程退出` 之后进度冻结 | **D1**（且 §2.1 就是它的物理原因） |
| 跑几小时才报「下载超时（180 分钟）」 | **D1** 的典型特征 |
| 拖动 / 断点续播到大文件深处才断 | P1 + P4 |
| `[proxy] 播放器已关闭连接，结束该请求` | 播放器先放弃了（正常收尾，不是错误） |

---

## 4. 已修（commit `5ebe654`、`131590d`）

**`5ebe654` —— 播放侧**

1. **P1**：60s 到点**只自救**（`KickUpstream()` 打断上游重发 Range），**保持连接继续等**，
   绝不截断响应体。播放器自己的读超时会断开重连、重发 Range ⇒ 等于一次**可恢复的**重试。
   另加 5 分钟绝对上限 + 每秒探测播放器是否已关连接（防残留 `Req` 压住 `_requests` 最小位置、
   害上游从旧位置白拉整段）。
2. **P2**：接线 `BodyStallMs` —— 已收到过字节（连接已过期）按 **8s** 判；
   一个字节都没收到（引擎还在下该区间）放宽到 **48s**，避免打爆 guest 串行 accept。
   到点 `break` 让外层循环重发 Range（外层每轮都从 `from = base + len` 重新请求）。
3. **§2.2** 两处 4MB 分配改 `ArrayPool` 池租（使用点均走**显式长度**，故安全）。

**`131590d` —— 下载侧**

4. **D1**：新增 `QemuHostRuntime.HasExited`（异常安全；与 `IsRunning` 分开是刻意的 ——
   避免把 `_runtime = null` 的主动收尾误判成崩溃）与 `Died` 事件；下载循环 **1s 一拍**判进程存活，
   死了立即返回**可续传**的失败，不再空转 180 分钟。
5. **P4**：块设备容量默认 **16GB → 64GB**。
   ⚠ 一次性代价：已存在的 16GB 镜像大小不匹配会被重建，块设备内已缓存数据丢一次
   （宿主 `btcache` 磁盘缓存不受影响）。

---

## 5. 待办（按性价比）

| 优先级 | 事项 | 说明 |
|---|---|---|
| **★★ P0** | **让 `/thunder-data` 从「长期缓存」变成「环形管道」** | 数据本来就已经由块设备通道落宿主（直读 4578 MB/s），guest 那份是**重复的**。改成 ~512MB 环形缓冲后：内存与文件大小**解耦**、`-m` 可降到 2GB 级、**提速约 1.5×**、**从根上消除 Android 被 LMK 杀**。需改 harness + initrd，必须真机验证 |
| **★ P1** | 交叉轮转 A/B 复测 `-m 2048` vs `-m 5120` | 现有数据噪声 ±21%，不能当结论（`qemu-tcg-tuning.md` §7.2 自己标注） |
| **★ P1** | `Append` 的 `new byte[count]` 池化 | 唯一未处理的大 churn（≈1GB/GB）。**不可直接 `ArrayPool`**：`_chunks` 用 `chunk.Length` 当长度参与 `ReadAt`/`Trim` 计算，必须同时引入并行的 `_chunkLens` |
| **P1** | 宿主监控 QEMU **RSS**，超阈值主动收会话 | 而不是等系统杀 |
| **P1** | Android 补 `OnTrimMemory` | `Platforms/Android` 下目前**完全没有**内存压力处理代码 |
| **P2** | 重启后自动恢复中断的下载（可加开关） | `DownloadManager.cs:958` 目前只把 `Downloading → Paused` |
| **P2** | 导出重试改指数退避 + 分类 | `PullToFileAsync`（`:522`）现为 5 次 × 2s 固定；404/416 不必重试 |
| **P3** | 看门狗条件放宽 | P3 本身未改（放宽有误报 KICK 风险，需实测后再动） |

---

## 6. 相关文档

- `magnet-streaming-cache-plan.md` —— 代理架构与三层缓存策略（本文件的前提）
- `playback-latency-analysis.md` —— 起播慢的根因（起播阈值对齐 TVBox）
- `qemu-tcg-tuning.md` —— TCG 调参（1.22×）、`-smp` 不要放大、§7.2 guest 内存杠杆
- `qemu-engine-performance.md` / `engine-network-interfaces.md`
