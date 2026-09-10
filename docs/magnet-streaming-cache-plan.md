# 磁力播放功能 — 缓存策略分析方案

> 目标：点击磁力集后**边下边播**，起播快、拖动顺、二次播放免重下。
> 现状：xb6v 等猫爪 web 规则源已能抓到"磁力下载"线路（`magnet:` URI 列表），
> [CatClawSourceProvider.ResolvePlayUrlAsync](../CatClawVideo.Core/Providers/CatClawSourceProvider.cs#L114-L118)
> 遇 magnet 直接抛"暂不支持（BT 引擎规划中）"——本方案补上这个引擎。

---

## 1. 为什么必须做"本地 HTTP 代理"（架构前提）

两端播放器 **Media3 ExoPlayer（Android）** 和 **WinUI MediaPlayer（Windows）** 都不能直接读 BT。
可行路径只有一条：**BT 引擎在本地提供流，播放器当普通网络视频播**。

```
播放器 (ExoPlayer / WinUI MediaPlayer)
    │  HTTP GET http://127.0.0.1:{port}/stream/{infoHash}/{fileIndex}
    │  要求：支持 Range 请求、精确 Content-Length
    ▼
BT 流式代理（本地 HTTP 服务，仅监听 127.0.0.1）
    │  Stream.Seek/Read 映射到 piece 拉取
    ▼
MonoTorrent 流式引擎（ClientEngine.AddStreamingAsync）
    │  StreamingPieceRequester：播放位最高优先级 + 前向窗口预取
    ▼
peers / DHT / trackers（piece 落盘到缓存目录）
```

**BT 引擎选型：MonoTorrent 3.x**（纯 .NET Standard，MIT，Android/Windows 同库无 native 依赖）。
决定性理由：2.0 起内置流式模式——`ClientEngine.AddStreamingAsync` 返回为流式配置的
`TorrentManager`，`TorrentManager.StreamProvider.CreateStreamAsync(file, prebuffer: true)`
给出**可 seek 的 Stream**，且其流式 piece 请求器天然实现"播放位优先、限制低优先级在途请求、
seek 快速重排"——这正是流畅播放的核心难点，不需要自己写 piece picker。

---

## 2. 缓存策略分析（核心）

缓存分三层看，每层的"策略"各不相同：

### 2.1 第一层：piece 网络下载（流式 picker 负责）

| 策略点 | 分析结论 |
|---|---|
| 优先级 | 播放头所在 piece = 最高优先；`StreamingPieceRequester` 内建，seek 自动重排 |
| 首尾 piece 预取 | `prebuffer: true` 必开——**mp4 的 moov atom 多在文件尾**（xb6v 站源压制尤其如此），不取尾片连元数据都读不到，这是"磁力起播慢"的第一大坑 |
| 预取窗口 | 按**秒**不按 MB（码率自适应）：默认前向 60s 播放量（1080p ≈ 90MB 上限封顶） |
| 已读 piece | 无需特殊处理——piece writer 本来就落盘，**回拖零下载** |
| 拖到未下载区 | 拖动即 HTTP Range → Stream.Seek → picker 换窗；代理层重新走一次"起播闸门"（见 2.3） |

### 2.2 第二层：内存缓冲

- MonoTorrent 内置内存盘缓存 `EngineSettings.DiskCacheBytes`：设 **32~64MB**，乱序到货的 block 先在内存拼 piece、增量哈希后落盘，显著减少随机小 IO（Android 闪存友好）。
- 代理层另设 **64~256KB 读写缓冲** 平滑网络 jitter，避免播放器把 Stream 读出碎 IO。

### 2.3 第三层：磁盘缓存（本方案的重点决策）

**方案对比：**

| 方案 | 说明 | 优点 | 缺点 |
|---|---|---|---|
| A. 会话临时目录 | 退出播放即删 | 实现最简 | 二次观看全冷启动；断点续播体验差 |
| **B. 全局配额 + LRU（推荐）** | 缓存根目录按 infoHash 建子目录，全局配额（默认 **6GB**），LRU 按"最后访问时间"清理 | 二次观看秒开；断点续播免重下；磁盘占用可控 | 需要清理机制（启动 + 播放结束两个时机，后台执行不卡 UI） |
| C. 收藏常驻 | 收藏影片永不清理 | 体验最好 | 与 B 组合才可控，v2 再做 |

**推荐 B，细节：**
- 目录：Windows `%APPDATA%\CatClawVideo\btcache\{infoHash}\`；Android `FileSystem.CacheLocation\btcache\{infoHash}\`。
  不用系统临时目录（防系统清理打断续看）。
- **FastResume**：每会话保留 `.fresume`（MonoTorrent 默认 20~30s 自动保存）→ 同片二次打开**跳过哈希检查**，配合已有 piece 秒级续播。
- 清理时机：App 启动时 + 每次播放结束检测配额；按目录最近访问时间从旧到新删，正在播放/上次播放的不删。

### 2.4 流畅播放的四个关键参数（调优清单）

1. **起播闸门**：metadata 就绪 + 首片尾部片到手后，再缓冲 **5~8 秒播放量** 才向播放器返回数据（HTTP 层"憋住"响应）。播放器读得快、代理放得慢，天然形成起播缓冲闸，避免刚起播就卡。
2. **拖动后闸门**：seek 后缓冲 3~5s 再放行，拖动体验连续不黑屏。
3. **速度监测**：下载速率 < 消耗速率 × 1.2 持续 10s → UI 显示"缓冲中"；接近 0 持续 30s → 提示"资源冷，建议换线路"。磁力没有时长元数据，码率用实测消耗速率（播放位置增速 × 平均读速）近似。
4. **Tracker 增强**：xb6v 站磁力自带 tracker 少。启动会话时**注入公共 tracker 列表**（opentrackr 等 10~15 个）+ DHT 常开 + PeX；DHT bootstrap 需 5~10s，与起播闸门**并行**进行，不叠加等待。

### 2.5 最容易被忽视的两条"缓存节约"策略

- **文件选择即节约**：磁力常是合集包。metadata 到手后按"视频扩展名 + 体积 + 集名匹配"选目标文件，其余文件 priority = None（**完全不下载**）。
- **同 torrent 多集复用**：剧集包的多集 = 同一 infoHash 的不同文件。会话期间保持 manager 活跃，切集只换 fileIndex，已下 piece 直接复用；代理层按 infoHash 复用 manager，切集时 Dispose 旧 Stream 再建新 Stream（MonoTorrent 同 manager 同一时刻仅允许一个活跃 Stream）。

---

## 3. 代理层与生命周期

- 端点：`GET /stream/{infoHash}/{fileIndex}`，必须实现 `Range` 透传、`Accept-Ranges: bytes`、精确 `Content-Length`（WinUI MediaPlayer 拖动强依赖 Range）。
- 引擎单例常驻（DI 注册）；离开播放页 30s 无复用 → Dispose Stream、Pause manager（**保留缓存与 fast-resume**）。
- 上传限速默认 256KB/s、总连接数 ~80（移动流量友好）；Android 进入后台暂停引擎。

## 4. 分阶段实施

| 阶段 | 内容 | 交付标准 |
|---|---|---|
| 1. 核心链路（✅ 已完成） | [BtStreamService](../CatClawVideo.Core/Services/BtStreamService.cs)（磁力→metadata→选文件→Stream）+ [BtHttpProxy](../CatClawVideo.Core/Services/BtHttpProxy.cs)（TcpListener 极简 HTTP）+ `ResolvePlayUrlAsync` 磁力分支接入 | xb6v 磁力线路可点播、可拖动 |
| 2. 体验 | 配额 LRU 清理 + FastResume + 速度监测 UI | 二次打开秒续；冷门资源有明确提示 |
| 3. 增强 | 同 torrent 多集复用 + 收藏常驻缓存（可选） | 剧集包切集不重新起播 |

**阶段 1 实现记录（与原方案差异）：**
- MonoTorrent 3.0.1 的 `EngineSettings`/`TorrentSettings` 为不可变对象，需经 `EngineSettingsBuilder`/`TorrentSettingsBuilder` 构建（API 与 2.x 文档示例不同）。
- 公共 tracker 注入只能在 **URI 字符串层**追加 `tr=` 参数（`MagnetLink.AnnounceUrls` 运行时只读）。
- `Priority.None` 在 3.x 更名为 `Priority.DoNotDownload`；文件序号用 `manager.Files.IndexOf(file)`（接口无 `Index` 属性）。
- 冒烟测试（webtorrent Sintel 种子，冷节点）：metadata 27.7s、Range 206 首包 28ms、中段 seek（50MB 处）拉流 4.4s —— 起播/拖动链路全通。
- 引擎与代理懒启动（首次磁力播放才初始化），不拖慢 App 首帧。

## 5. 风险与对策

| 风险 | 对策 |
|---|---|
| MonoTorrent 流式 seek 历史缺陷（如 issue #402 seek 后降速，2.0 后已修） | 集成后重点回归"拖到中段/尾部再拖回"场景，锁版本不追新 |
| 冷门资源无 peer | 技术上无解；速率归零检测 + 明确 UI 提示换线路 |
| metadata 等待不可控（5~30s） | 起播状态可视化（复用 WatchPage 缓冲指示），30s 超时提示 |
| 合规 | 仅播放技术，不内置任何资源；免责声明已有 |

---

## 6. 排障实录：磁力零速度（2026-09-10 已修复）

**症状**：用户报"磁力下载没速度，Motrix 下同样视频很快"；bt.log 现场证据 `state=Metadata 节点=0` 持续 40s+。

**诊断路径**（可复用）：
1. 读 `{AppData}/logs/bt.log`（BtFileLog 落盘）——确认卡在 Metadata 且 **0 节点**（连 peer 候选都没有）。
2. 反射 dump 引擎状态：`engine.IsRunning / Dht.State / Dht.NodeCount`、`manager.Peers.Available / OpenConnections`、`trackerManager.Tiers → Trackers` 的 `Status/FailureMessage`。
3. **同进程对照实验**（排除"进程级网络限制"）：原生 `UdpClient` 手发 BEP15 `connect` 包 → ✅ UDP 出站完全可用。
4. **逐项验证 peer 来源**：
   - DNS 污染检查：`router.bittorrent.com`/`router.utorrent.com` 被解析到 **Facebook IP**（31.13.x.x）→ DHT bootstrap 失效（`Dht.State=Initialising`、`NodeCount=0` 永久）。
   - tracker 手测（BEP15 UDP announce，python 脚本）：**19/20 可用**、Sintel 有 112 做种 —— 网络没问题。
5. **二分实验定位**：手工指定 6 个可达 tracker → **15s 拿到 metadata、56 候选 peer**；换回 23 个全长列表 → 60s 零节点。

**根因**：`PublicTrackers` 用了 ngosang「best」全长列表（23 个），其中混有 **DNS 不可解析/污染的域名**（bt.endpot.com、tracker.gbitt.info、tracker.bindu.corp.google.org 等）。同一 tier 内 announce 串行，**坏 tracker 的超时拖垮整轮 peer 获取**。

**修复**（`BtStreamService`）：
1. `PublicTrackers` 精简为 **6 个实测配方**（opentrackr / open.stealth.si / explodie / exodus.desync + foreverpirates(HTTPS) + openbittorrent(HTTP)），并写明"不要再加看起来能通的"的列表纪律。
2. 连接能力对齐 Motrix：`MaxConnections` 60→150、**`MaximumHalfOpenConnections` 8→60**（MonoTorrent 默认仅 8，是隐性瓶颈）、`MaxUploadRate` 256KB/s→1MB/s（BT 互惠，上传过低会被 choke）。
3. Android 侧 `MaxConnections` 50→120、半开 40。

**验证**：Sintel 磁力冷启 6s metadata → 18s 已下 95.6MB、峰值 **8.1MB/s**（与 Motrix 同级）。

**MonoTorrent 3.0.1 API 备忘**（反编译确认）：
- `ClientEngine.StartAsync()` 是 **internal**，外部无法直接启动引擎；**`TorrentManager.StartAsync()` 会触发引擎启动**（DHT 随之从 NotReady → Initialising）。`ClientEngine.StartAllAsync()` 只是"启动所有 torrent"，不是引擎启动。
- `engine.Dht` 为 `DhtEngineWrapper`（只读封装：State/NodeCount/Monitor），**无法从外部注入 DHT 节点**；bootstrap 依赖 `AutoSaveLoadDhtCache` 的节点缓存文件 + 内置域名（被污染即永久 0 节点）。
- `engine.DownloadMetadataAsync(magnet, ct)` 返回 `ReadOnlyMemory<byte>`（metadata 字节），需 `Torrent.Load(bytes.ToArray())`。
- `ConnectionMonitor.DownloadSpeed/UploadSpeed` 已过时 → 用 `DownloadRate/UploadRate`。
- `ITrackerManager` 无 `Trackers` 属性 → 遍历 `Tiers` → `TrackerTier.Trackers`。

---

## 6. 排障实录：磁力零速度（2026-09-10 已修复）

**症状**：用户报"磁力下载没速度，Motrix 下同样视频很快"；bt.log 现场证据 `state=Metadata 节点=0` 持续 40s+。

**诊断路径**（可复用）：
1. 读 `{AppData}/logs/bt.log`（BtFileLog 落盘）——确认卡在 Metadata 且 **0 节点**（连 peer 候选都没有）。
2. 反射 dump 引擎状态：`engine.IsRunning / Dht.State / Dht.NodeCount`、`manager.Peers.Available / OpenConnections`、`trackerManager.Tiers → Trackers` 的 `Status/FailureMessage`。
3. **同进程对照实验**（排除"进程级网络限制"）：原生 `UdpClient` 手发 BEP15 `connect` 包 → ✅ UDP 出站完全可用。
4. **逐项验证 peer 来源**：
   - DNS 污染检查：`router.bittorrent.com`/`router.utorrent.com` 被解析到 **Facebook IP**（31.13.x.x）→ DHT bootstrap 失效（`Dht.State=Initialising`、`NodeCount=0` 永久）。
   - tracker 手测（BEP15 UDP announce，python 脚本）：**19/20 可用**、Sintel 有 112 做种 —— 网络没问题。
5. **二分实验定位**：手工指定 6 个可达 tracker → **15s 拿到 metadata、56 候选 peer**；换回 23 个全长列表 → 60s 零节点。

**根因**：`PublicTrackers` 用了 ngosang「best」全长列表（23 个），其中混有 **DNS 不可解析/污染的域名**（bt.endpot.com、tracker.gbitt.info、tracker.bindu.corp.google.org 等）。同一 tier 内 announce 串行，**坏 tracker 的超时拖垮整轮 peer 获取**。

**修复**（`BtStreamService`）：
1. `PublicTrackers` 精简为 **6 个实测配方**（opentrackr / open.stealth.si / explodie / exodus.desync + foreverpirates(HTTPS) + openbittorrent(HTTP)），并写明"不要再加看起来能通的"的列表纪律。
2. 连接能力对齐 Motrix：`MaxConnections` 60→150、**`MaximumHalfOpenConnections` 8→60**（MonoTorrent 默认仅 8，是隐性瓶颈）、`MaxUploadRate` 256KB/s→1MB/s（BT 互惠，上传过低会被 choke）。
3. Android 侧 `MaxConnections` 50→120、半开 40。

**验证**：Sintel 磁力冷启 6s metadata → 18s 已下 95.6MB、峰值 **8.1MB/s**（与 Motrix 同级）。

**MonoTorrent 3.0.1 API 备忘**（反编译确认）：
- `ClientEngine.StartAsync()` 是 **internal**，外部无法直接启动引擎；**`TorrentManager.StartAsync()` 会触发引擎启动**（DHT 随之从 NotReady → Initialising）。`ClientEngine.StartAllAsync()` 只是"启动所有 torrent"，不是引擎启动。
- `engine.Dht` 为 `DhtEngineWrapper`（只读封装：State/NodeCount/Monitor），**无法从外部注入 DHT 节点**；bootstrap 依赖 `AutoSaveLoadDhtCache` 的节点缓存文件 + 内置域名（被污染即永久 0 节点）。
- `engine.DownloadMetadataAsync(magnet, ct)` 返回 `ReadOnlyMemory<byte>`（metadata 字节），需 `Torrent.Load(bytes.ToArray())`。
- `ConnectionMonitor.DownloadSpeed/UploadSpeed` 已过时 → 用 `DownloadRate/UploadRate`。
- `ITrackerManager` 无 `Trackers` 属性 → 遍历 `Tiers` → `TrackerTier.Trackers`。

## 7. Tracker 列表动态化 + 设置页（2026-09-10）

**问题**：第 6 节的"6 个硬编码配方"同样会过期——需要可持续的列表来源。

**方案**（`BtTrackerSource`）：
1. **来源**：ngosang/trackerslist 的 `trackers_best_ip.txt`（**IP 形式，天然免疫 DNS 污染**）+ `trackers_best.txt`；
   多镜像回退：cdn.jsdelivr.net → fastly.jsdelivr.net → git.yylx.win 代理 raw（官方 raw 在国内被污染）。
2. **可达性过滤**：拉到的候选并发做 **BEP15 connect 探测**（UdpClient，3s 超时，12 并发），只留可达项；
   可达 < 3 时回退用未过滤列表；总量上限 14（同 tier 串行，越多越慢）。
3. **缓存与更新**：`{AppData}/trackers.txt`（首行 `# updated=<unix>`），TTL 24h；启动预热 + 设置页"立即更新"。
4. **保底**：联网失败时用内置 6 配方（`BtTrackerSource.BuiltInFallback`）。

**实测**：首次拉取+过滤 **3.5s**（20 候选 → 18 可达 → 采用 14），二次调用 0ms；Sintel 磁力 5s 拿到 metadata 并开始下载。

**设置页**（`BtSettingsPage`，下载管理右上角 ⚙ 进入，布局照搬 Motrix）：
默认下载路径 / 传输限速（上·下 KB/s）/ BT 设置（保存元数据、自动开始、持续做种、分享率、做种时间）/ 任务管理（最大任务数、每服务器最大连接数、自动跳转、完成通知、删除确认）/ **Tracker 服务器**（列表展示 + 每天自动更新 + 立即更新）/ 监听端口（UPnP-NAT-PMP、BT、DHT）/ User-Agent。
持久化 `{AppData}/bt-settings.json`（`BtSettings`）；改端口/限速/连接数保存后 `RecreateEngineAsync()` 重建引擎生效。

**不适用项**（aria2 专属，已在页面注释说明）：RPC 监听端口/密钥、迅雷链接（thunder://）、默认客户端协议。

---

## 8. 排障实录：磁力「有 metadata 但零速度」（2026-09-10 已修复，根因与第 6 节不同）

**症状**：同一个磁力，Motrix 有 556KB/s（748 做种），猫爪 `state=Downloading` 但
`已下=0 速度=0B/s 节点=1`，几分钟后节点掉到 0 再没恢复。

**关键：这次不是 tracker 的问题**（第 6 节的教训已生效）。逐项实测：

| 检查项 | 结果 |
|---|---|
| UDP tracker announce（36 个候选） | 29 个可达；单个 tracker 最高返回 192 seeds，合计 2242 做种 / 2407 条 peer 记录 |
| MonoTorrent 侧 tracker 状态 | 12 个 `status=Ok`，announce 完全正常 |
| 防火墙 | `CatClawVideo.Maui` 已有 Allow 规则 |
| UDP 出站 | 正常（BEP15 connect 有响应） |
| peer TCP 可达性 | **仅 6/60**，失败集中在端口 15000 的国内 IP |
| **DHT** | **`Initialising/0` → `NotReady/0`，始终 0 节点** |

**根因：DHT 完全无法 bootstrap。** MonoTorrent 的内置 router 域名在国内均不可用：

- `router.bittorrent.com` → 解析到 **31.13.85.53（Facebook 网段）**，ping 超时
- `router.utorrent.com` → 两次查询解析结果不同（199.59.149.203 / 108.160.166.148），均超时
- 只有 `dht.transmissionbt.com`（212.129.33.59、87.98.162.88）能正常响应

首次运行 `dht_nodes.cache` 为空（内容就 2 字节 `le`）→ 没有任何种子节点 → DHT 起不来。
失去 DHT 后只剩 tracker 一条 peer 来源，而 tracker 列表里混着大量 NAT 后不可达的国内节点，
凑不出可用连接，于是表现为「元数据拿得到、连接恒为 1、下载恒为 0」。

**修复**（`BtStreamService.SeedDhtNodeCache`）：内置 10 个 DHT 种子节点
（紧凑格式，26 字节/节点 = 20 字节 node-id + 4 字节 IP + 2 字节端口，取自
`dht.transmissionbt.com` 的 find_node 响应），引擎创建前若缓存节点数 < 8 则播种。

**实测效果**（同一磁力、同一台机器）：

| 指标 | 修复前 | 修复后 |
|---|---|---|
| DHT | `Initialising/0` → `NotReady/0` | **`Ready/67` → 71** |
| peer 连接数 | 1 | **34** |
| 下载速率 | 0 B/s | **峰值 3.44 MB/s** |
| 60 秒已下载 | 0 | **43 MB** |

**维护提示**：种子节点会失效，但只要 DHT 能启动，它会自行爬升，MonoTorrent 退出时把扩展后的
节点表写回 `dht_nodes.cache`，后续启动直接受益。仅当缓存被清空时才需重新播种。
若发现 DHT 又长期停在 0 节点，重新从 `dht.transmissionbt.com` 抓一批节点更新
`BtStreamService.DhtSeedNodes` 即可。

**可复用的诊断路径**（本次用到的顺序）：
1. 读 `{LocalAppData}/CatClawVideo.Maui/CatClawVideo.Maui/Data/logs/bt.log`，看 `[dl]` 行的
   已下/速度/节点三个数 —— 「节点恒为 1 且已下为 0」是 DHT 失效的指纹（区别于第 6 节的「节点恒为 0」）
2. 检查 `{AppData}/CatClawVideo/btcache/engine/dht_nodes.cache` 大小：2 字节 = 从未拿到节点
3. 用 BEP15 UDP announce 实测 tracker（含热门种子做对照组），排除 tracker 问题
4. 向 DHT bootstrap 域名发 BEP5 `ping`，确认是否被 DNS 污染
5. 抓一批可用节点写进 `dht_nodes.cache` 做 A/B 对照，一次就能定位

> 注：第 6 节与本节是两个独立的坑 —— 第 6 节是「tracker 列表混入坏 tracker 拖垮整轮 announce」，
> 本节是「DHT bootstrap 域名被污染导致 DHT 从未启动」。两者症状相近（都零速度），
> 但前者看 `tracker 明细` 能发现，后者必须查 DHT 状态与 `dht_nodes.cache`。

---

## 9. 排障实录：整包下载零速度（2026-09-10 已修复，第 8 节之后又踩的一个坑）

**症状**：第 8 节修完后流式播放正常了（能跑 3MB/s），但**下载管理里的整包下载仍然一点速度都没有**：
`state=Downloading 已下=0 速度=0B/s 实连=1 候选=0`。

**定位过程**：DHT 明明已是 `Ready/71`、tracker 也注册了 6 个，为什么还是 0？
最后靠对照实验卡住差异 —— 同一个引擎、同一时刻、同一个磁力：

| 调用方式 | 连接数 | 候选 peer | 速率 | 已下载 |
|---|---|---|---|---|
| `AddAsync(magnet, saveDir)`（默认 settings） | 1~4 | 104 → **0** | **0 B/s** | **0 字节** |
| `AddAsync(magnet, saveDir, settings)` | 13~17 | 173 → 96 | **15.7 MB/s** | **778 MB** |

**根因**：`AddAsync(magnet, saveDir)` 不传 `TorrentSettings` 时用的**默认值**会让 manager 完全下不动。
最坑的是它的 `state` 依然是 `Downloading`、`AllowDht=True`、tracker 也挂在 `TrackerManager` 上 ——
所有表面指标都「正常」，只有「已下 0 字节」这一个事实是真的。
流式路径（`AddStreamingAsync`）一直是显式传 settings 的，所以这个坑只在整包下载上暴露。

**修复**（`BitTorrentDownloadService.StartAsync`）：显式构造并传入 TorrentSettings：

```csharp
var torrentSettings = new TorrentSettingsBuilder
{
    MaximumConnections = Math.Min(_bt.MaxConnections, 200),
    UploadSlots = 6,
    AllowDht = true,
    AllowPeerExchange = true,
}.ToSettings();
manager = await _engine.AddAsync(link, saveDir, torrentSettings);
```

**顺带修掉的 DHT 播种缺陷**：第 8 节的播种逻辑只在「缓存为空」时生效，但 MonoTorrent 退出时
会把当前路由表写回缓存。若某次 DHT 没能爬起来（或残留一批已全死的节点），长度检查会误判
「够用」而跳过播种 → DHT 再也起不来（本次 19:00 实测踩到）。现改为**并入**：每次启动解析现有缓存，
把内置种子按 IP:端口 去重合并进去，既保住 DHT 爬升出的节点表，又保证始终有种子可用。

**诊断要点（新增）**：判断「下载不动」时**不要只看 state**。
`state=Downloading` + `实连` 很少（≤4）+ `候选` 从上百衰减到 0 + `已下 0 字节`
= manager 根本没在干活，优先怀疑 **TorrentSettings 没传/传错**，而不是网络。
