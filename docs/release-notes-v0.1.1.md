猫爪影视 v0.1.1 —— 本次更新以 **Windows 端磁力边下边播** 为主线：让 PC 上原本播不动 / 播不顺的磁力资源真正跑起来，并修掉一批「换集死锁」「退出播放页后仍在后台下载」这类硬伤。

> 自 v0.1.0（2026-09-13）以来共 **85 个提交**、336 个文件变更（+20607 / −3281），跨度 09-13 ~ 09-18。

---

## 一、Windows：磁力边下边播（本次最大块）

### 1. 迅雷 P2SP 引擎跑进 QEMU —— PC 上磁力真正能播了
- 把 AArch64 Android 的迅雷下载引擎跑在 `qemu-system-aarch64` 里（真实 Linux 内核 + bionic + 迷你 JNI 垫片），宿主用 C# 驱动控制通道、取回播放地址，**用户零安装**（不装 WSL、不装 qemu、不装 Java）。运行时随包分发在 `ThunderRuntime/`，溯源见 `PROVENANCE.md`。
- 实测边下边播打通：P2P 约 2.5 MB/s，宿主 HTTP 206 拉到真 MP4。
- 引擎身份签名对齐真机（`peerid` = MAC+004V / `setMiuiVersion` / `PhoneModel` / `Identify2.txt`）——此前磁力任务被判死（114004）即由此而来；`guid` 用 `XLUtil.generateGuid`（IMEI_MMAC）修正。
- 补齐 JNI 垫片缺口（对象数组 / 布尔字段 / `GetObjectField`），引擎文件列表不再被丢弃；`/thunder-data` 挂**真 tmpfs**（ramfs 空间上报不可信会让引擎静默不下数据）。
- A/B 实证：磁力零速度的唯一元凶是创建任务时多发 4 个引擎调用，已移除；裸 `btih` 亦可跑满速（`dn=` 参数无关）。

### 2. 架构简化：磁力只走迅雷
- **完全移除内置 BT 引擎（MonoTorrent）**。公共 BT swarm 实测已死（6.6GB 资源 metadata 3.3s 到手，但预缓冲 60s 只有 9.9 KB/s、peer 0），保留它只会与迅雷抢带宽。现在磁力链路为：**QEMU 本地迅雷引擎（首选）→ 迅雷网盘 API（兜底）**。

### 3. 播放流畅度
- **宿主侧「读前缓存代理」**：连接开销 210ms → 0ms，1MB 分块读 229ms → 2ms，并发读取不再被串行饿死，缓冲命中吞吐 ≥330MB/s。
- **磁力点播磁盘缓存**：4MB 分块落盘 + LRU 超限清理；重看 / 换集回看直接磁盘秒供，不再依赖引擎易失的 tmpfs。**上限在设置页可调（5 / 10 / 20 / 30 / 50 GB，默认 20GB，改完即时生效）**。
- 引擎按需区间供数：seek 到哪、引擎优先下哪；片尾 256KB 读得到真数据（Cues 可取），不再「播到结尾卡死」。

### 4. 换集 / seek 稳定性（修掉一批死锁）
- **同种子换集绝不重建任务**：老任务本就全选下载整个种子，换集只把媒体口切到新文件路径 —— 根治「重建 → 9128 → 任务自杀 → 播放彻底死锁」。
- 9128（同种子任务已存在）改**退避重试**自愈，不再 127ms 后立即重建。
- 详情页磁力展开（探测）**让位活跃播放会话** —— 根治 9111 与「播放被顶掉」。
- 选集按集号去重排序；换集立即掐灭旧画面，不再白拉旧流。
- 起播尾部探测 416 致 `MP4 frames:0 / Duration=0` 秒跳下一集 —— 已修。

### 5. 退出播放页 / 取消下载后，不再继续下载 ✅ 本次新增
- 迅雷任务交出播放地址后会**自己一直下**，而宿主对「播放器是否还在看」一无所知。现在播放页退出即冻结引擎所在 VM：**下载当场停**（vCPU 冻住、对端 TCP 立刻掉速），而任务与已下载数据全部保留，回来点同一部剧 **3~5 秒续播**，不必重建任务、不必冷启动。
- 下载页「取消」同样真正停任务（此前只是宿主不再轮询，引擎照旧跑）。
- 真机压测 15 轮「进播放页 → 退出」全过：冻结触发 15/15；播放期 QEMU CPU 增量约 11.5~15.2 秒/8 秒（满载，下载 2.5~4.5 MB/s），冻结后仅 15~125 毫秒（≈静默），冻结期 guest 零上报；无任务死亡、无 9128、无恢复重试。

### 6. 自动连播
- 观看页补齐自动连播（此前只有全屏播放页有）；去掉卡住流程的模态弹窗，自然播完静默 3 秒直接下一集。

### 7. 起播耗时定位
- 新增 `docs/playback-latency-analysis.md`：定位「同站点 TVBox <10s vs 猫爪数分钟」的三条结构性差异（起播阈值 / 取源通道 / 直链路径）。
- 起播提速：宿主按确定路径直取种子，绕开 guest 侧 5 秒上报节拍（9 条磁力白等 40 秒，占起播 65 秒的 69%）。

---

## 二、Windows：爬虫站点可用性（Java 桥 + Guard 加固包）

- **桌面端 unidbg 解 Guard 加固包**：模拟 ARM64 跑原生解密 `.so` 还原明文 dex，替换原先「只能换非 Guard 同族 jar」的降级路径。实测该壳无反模拟检测，解壳 ~3s/包。
- 修「应用内 Guard 解壳永久卡死」：子进程 stdin 不是 EOF，unidbg 交互式调试器一直在等输入。
- 桥 JVM 关闭字节码校验（`-Xverify:none`）：dex2jar 产物缺栈帧让 **5 个站点从整站不可用变可播**。
- 补齐 android stub（顶层类 / 内部类 / 枚举 + `AES PKCS7 别名 Provider`、`ContentResolver`、`Build`、`ActivityThread`、`WifiInfo` 等），并补宿主本地 `/proxy` 服务 —— 修「播放失败：Invalid URI: Invalid port specified」。
- 「导入源后只有 3 个源」根治：`IsSupported` 拆成「桥可用 / Guard 可解」两级 + JavaBridge 随包分发 + 多候选目录择能力最全者。
- 修复站点整站加载失败、搜索丢掉迟到结果等爬虫层问题。
- 新增「手机当解析节点」：PC 借手机的 spider 能力跑 Guard 站点（局域网配对 + 口令鉴权），免疫壳升级。
- 荐片（`csp_Jianpian`）宿主侧 P2P 支持，真机可播。

## 三、Windows：工程化与体验

- **Debug 与 Release 数据彻底隔离**：统一 `Core.AppPaths`，首次从旧位置自动迁移（订阅 / 历史 / 凭据 / 网盘状态）。BT 设置、tracker 缓存、脚本缓存、封面缓存、下载任务清单统一走 `AppPaths`。
- 站点列表本地缓存 `sites-cache.json`：启动同步秒读，修「首屏闪一下『还没有可用的源』」与「重启后站点列表为空」。
- 收藏/搜索：收藏源失效改为直接跨源搜回该片（不再弹「请重新收藏」）；长按/右键卡片可直接取消收藏；搜索结果标注来源站点。
- 移除仓库内自研源文件与生成器，源一律靠运行时订阅导入。

## 四、Android 端

- 内置迅雷下载引擎，磁力播放优先走 P2SP；磁力剧集按种子文件列表展开（从「3 集」还原成「11 集」）。
- **修复 Android 目标编译断裂**：`IPreferredMagnetEngine` 增加 `IsBusy` 时漏了 Android 实现（`CS0535`），此前 Android 侧一直编不过 —— 本版已补齐。
- HLS 地址在 query 里时显式指定 MIME，修 `UnrecognizedInputFormatException`。
- 毒舌破盾失败修复（`HttpURLConnection` 对 850 状态码抛 `FileNotFoundException`）。
- 跨源搜索增量上屏、可播计数口径对齐。

## 五、文档

- 新增 `docs/playback-latency-analysis.md`（起播慢根因）、`docs/magnet-streaming-cache-plan.md`（缓存方案）、`docs/tvbox-parity-plan.md`、`docs/饭太硬接口分析.md`。

---

## 产物说明

| 文件 | 平台 | 说明 |
|---|---|---|
| `catclaw.video-0.1.1-Setup.exe` | Windows | 安装版（self-contained，无需另装 .NET 运行时） |
| `com.catclaw.video-Signed.apk` | Android | arm64-v8a 签名包，Android 12 (API 31) 及以上 |

> Windows 端的磁力播放需要随包的 `ThunderRuntime/`（QEMU + 内核 + initrd），安装包已包含，无需额外下载。
> **磁力播放依赖第三方迅雷引擎的可用性**；若某源起播失败，可在同剧的其他可播站点间自动换源。

## 相关链接
- 仓库：https://github.com/kankejiang/CatClawVideo
- 猫爪音乐（姊妹项目）：https://github.com/kankejiang/CatClawMusic
- QQ 交流群：855383639（可通过 QQ 搜索群号加入）

本项目仅为播放器壳，不内置任何片源，也不提供、不存储、不上传任何影视内容；所有源均由用户自行配置并对所添加的源及观看内容承担全部责任。
