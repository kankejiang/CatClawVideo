# 「同一部剧同一站点：TVBox <10s vs 猫爪数分钟」根因分析

> 实测日期：2026-09-16　数据来源：`%APPDATA%\CatClawVideo.debug\logs\bt.log` + `home-debug.log` + 代码 + TVBoxOSC 参考源码

## 一、结论

慢**不在播放器渲染**，而在三处**结构性差异**：

| # | 差异 | TVBox | 猫爪 | 影响 |
|---|---|---|---|---|
| 1 | **起播阈值** | 任务一到「下载中」立刻把本地地址交给播放器（`Thunder.play()`：`case 1/4/2 → callback.play(getLoclUrl(...))`），**零预缓冲** | 磁力要过 QEMU：VM 冷启动 + `WaitFirstPollAsync(90s)` + `.torrent` 拉回展开 + 任务创建 + 元数据/首片；BT 兜底还有 `PrebufferTimeout = 60s` | 起播时间差**几十秒~数分钟** |
| 2 | **取源通道** | 迅雷 SDK **原生 in-process**，且带资源通道（diag 有 `Scdn`/`P2S` 字段） | 迅雷跑在 **QEMU(TCG)** 里；实测 diag `P2S=0 P2P=4222995 Scdn=0` —— **只有纯 P2P**（资源接口被 `EXTRA` 环境变量门控，默认不发） | 你截图 231 Mbps vs 猫爪 ~50 Mbps |
| 3 | **直链/网盘路径** | 播放器链路会把 URL 交迅雷（`Thunder.isSupportUrl` → magnet/thunder，网盘走迅雷下载器） | 直接把 URL 交给 FFmpeg（日志 `[player] FFmpeg 打开源：https://...`）→ libavformat 的 HTTP 是**单连接** | 单连接 vs 多连接 ≈ 4~5 倍 |

## 二、证据

### 2.1 猫爪这边（实测日志）

```
[23:44:45.129] [路由] 📔厂长┃不卡 是 Guard 站点 → 走手机解析节点（http://10.0.0.10:8899）
[23:44:47.232] [远程] playerContent 失败，回退本地：由于目标计算机积极拒绝，无法连接。
[23:44:59.506] [player] FFmpeg 打开源：https://media-qhxn-fj-home.qh6oss.ctyunxs.cn/FAMILYCLOUD/….mp4
[23:45:01.109] [player] FFmpeg MSS 就绪
[23:45:01.171] [player] MediaOpened
```

- **每次调用都先试手机节点**（该次 2.1s 白等；一次完整播放有 home/category/detail/player 等多轮）
- 直链由 **FFmpeg 直接打开**（无多连接下载器参与）
- 磁力任务侧：`已下载=1297927196/3035188555 速度=4222995`、`speed P2S=0 P2P=4222995 Scdn=0`（**4.2 MB/s 纯 P2P，无加速通道**）

### 2.2 TVBox 这边（参考源码）

```java
// app/src/main/java/com/github/tvbox/osc/util/thunder/Thunder.java
public static boolean play(String url, ThunderCallback callback) {
    currentTask = XLTaskHelper.instance().addTorrentTask(...);
    while (true) {
        XLTaskInfo taskInfo = XLTaskHelper.instance().getBtSubTaskInfo(currentTask, idx).mTaskInfo;
        switch (taskInfo.mTaskStatus) {
            case 1: case 4: case 2:                    // 下载中 / 完成
                callback.play(XLTaskHelper.instance().getLoclUrl(path));   // ← 立刻给地址
                return;
        }
        Thread.sleep(1000);
    }
}
```
→ **下载一开始就把本地地址交给播放器**，由播放器自己边读边等，没有「攒够 N MB 才起播」的门槛。

### 2.3 猫爪 QEMU 侧的固定开销（代码）

```csharp
// QemuThunderEngine.cs
if (_runtime is null || !_runtime.IsRunning) {
    if (!await _runtime.StartAsync(ct)) return false;                  // QEMU 冷启动
    var ready = await _server.WaitFirstPollAsync(TimeSpan.FromSeconds(90));  // guest 首次取任务
    if (!ready) { Log("VM 已启动但 90s 内 guest 未连上控制端"); return false; }
}
// Phase 1：下发磁力并把 .torrent 拉回来展开文件列表（成功即缓存会话）
```
+ `BtStreamService.PrebufferTimeout = 60s`（BT 兜底路径的起播预缓冲上限）

## 三、建议的修法（按性价比排序）

| 优先级 | 修法 | 预期收益 |
|---|---|---|
| **P0** | **对齐 TVBox 的起播阈值**：任务进入「下载中」即返回代理地址（`QemuStreamProxy` 已能承接边下边播），把「等数据」下放给播放器 | 磁力起播从 数分钟 → 十几秒 |
| **P0** | **手机解析节点加熔断**：连续失败 N 次后 5 分钟内直接用本地（或先用 300ms 健康探针） | 每次播放省 ~5-10s |
| **P1** | **直链/网盘走多连接下载**：给 `SpiderProxyServer` 的直取路径（或新增下载器）加**并发 Range 分片**，播放器从本地代理读 | 直链吞吐 ~50 Mbps → 对齐 231 Mbps |
| **P1** | **VM 常驻保活**：确认空闲回收阈值（`_lastActiveUtc`），避免连续播放时反复冷启动 | 省一次 VM 启动 |
| **P2** | 重新评估迅雷资源通道（`XLSetTaskAllowUseResource` 等，现由 `EXTRA` 门控）：当年「零速度」的结论可能受身份/调用时机影响 | 若 Scdn/P2S 生效，P2P 4.2 MB/s → 逼近 TVBox |

## 四、怎么判断自己碰到的是哪一种

看日志关键字即可定位：

| 日志 | 含义 | 对应修法 |
|---|---|---|
| `[player] FFmpeg 打开源：https://…` | **直链/网盘**路径，单连接拉流 | P1 多连接下载 |
| `[ctrl] ✅ 播放地址 = http://127.0.0.1:<port>/…thunder-data…` | **磁力**路径（QEMU 迅雷） | P0 起播阈值 + P2 资源通道 |
| `[路由] … 走手机解析节点` + `回退本地` | 手机节点不可达，**每次调用白等** | P0 熔断 |
| `VM 就绪（媒体口 …）` | 该次播放**付了 VM 冷启动** | P1 VM 保活 |
