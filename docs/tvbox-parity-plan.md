# TVBox 功能复刻评估与路线图

> 参考源码：`D:\Code\.workbuddy\refs\TVBoxOSC`（191 个 java 文件；`app` = 应用 + `player` = DKVideoPlayer + `catvod/crawler` 仅 4 个类）
> 评估日期：2026-09-16　评估对象：CatClawVideo（`net11.0-android` / `net11.0-windows`）

## 一、结论先说

| 问题 | 回答 |
|---|---|
| 能不能复刻？ | **能。** 核心链路（订阅 → 爬虫 → 搜索 → 详情 → 解析 → 播放）已基本对齐，个别点**超过** TVBox |
| 要不要「完整」？ | **不要逐项对齐。** TVBox 是 Android TV 遥控器交互（方向键+焦点），本项目是手机/桌面触屏，交互模型不同；照搬 UI 是负收益 |
| 真正的缺口 | 集中在 **4 个独立子系统**：① 直播 ② 网页解析内核 ③ 代理协议完整性 ④ 播放器能力 |
| 工作量粗估 | 核心已完成的约是「基础盘」；补齐 4 个子系统是**数周级**项目，建议按价值分 4 期推进 |

## 二、逐模块对照

| 模块 | TVBoxOSC 实现 | CatClawVideo 现状 | 差距 |
|---|---|---|---|
| **jar 爬虫运行时** | `crawler/JarLoader` + dex2jar | ✅ `JavaSpiderRuntime`(Windows 走常驻 JVM 桥) / `DexSpiderRuntime`(Android) | 🔶 零散 Android 桩（见第五节），**WebView 解析链** |
| **JS 爬虫（drpy）** | QuickJS | ✅ `DrpyJsSpiderRuntime`（Jint 实现 drpy2） | 🔶 引擎不同（QuickJS vs Jint），需 API 面 parity 抽查 |
| **解析（parse）** | `JsonParallel` / `snifferMatch` / `mixUrl` | ✅ `TvBoxParseEngine`（已逐条移植：`IsVipUrl`/`IsVideoFormat`/`MixUrl`/`SplitReqHeader`/`ExtractPlayFromJson`/并发多接口）+ `WebProbeResolver` + `CdnDefendSolver` | ✅ 语义对齐 |
| **本地代理** | `ControlManager` + spider 侧 `Proxy` | 🔶 `SpiderProxyServer`（`do=ck` 心跳 / `type=302` / 直取 + m3u8 分片与 `EXT-X-KEY` 改写 / Range；多端口 6677·9978·9997-9999） | ❌ `do=m3u8` **回调 `spider.proxy(Map)`**（Android 侧 `JianpianP2P` 已有此设计，Windows 缺）<br>❌ 直播代理、广告段过滤（`&ad=false` 只是收参数没实现） |
| **订阅管理** | `ApiDialog` + `ApiHistoryDialog` | ✅ `TvBoxSubscriptionManager` + `SourceConfigPage` | 🔶 多订阅历史/快速切换 |
| **搜索** | `SearchActivity` + `QuickSearchDialog` | ✅ 跨站聚合、边搜边出、**来源站点角标**、迟到结果补收 | ✅ **超出**（TVBox 无站点标注） |
| **首页/分类** | `HomeActivity` + `GridFilterDialog` | ✅ 分类 chips + 「切换源」 | ❌ **筛选器**（演员/年份/地区多维筛选） |
| **详情/播放** | `DetailActivity` / `PlayActivity` | ✅ `WatchPage`（选集/线路/收藏/分享/全屏） | 🔶 线路与选集交互细节 |
| **直播** | `LivePlayActivity` + 频道组/项适配器 + `LivePasswordDialog` + 直播设置 | ❌ **完全没有** | ❌ 一整套子系统 |
| **历史/收藏** | Room(`VodRecord`/`VodCollect`) | ✅ `VideoDatabase` | ✅ |
| **下载** | 弱（3 处） | ✅ 完整 `DownloadsPage`/`DownloadDetailPage` + BT/HTTP | ✅ **超出** |
| **磁力/迅雷** | `util/thunder` + `libs/thunder.jar` | ✅ **QEMU 跑 ARM64 迅雷 SDK** + MonoTorrent 兜底 + 流式代理 | ✅ **超出**（TVBox 只做下载） |
| **网盘** | 无（靠蜘蛛） | ✅ `ThunderPanEngine` 兜底 | ✅ **超出** |
| **投屏/推送** | `PushActivity` + `CustomWebReceiver` | ❌ | ❌ |
| **手机遥控** | `RemoteDialog` + `RemoteServer` | 🔶 `LinkServer`（只做配对，无遥控） | ❌ 遥控播放/进度同步 |
| **弹幕** | 3 个文件（很弱） | ❌ | ⚪ 低优先级 |
| **字幕/倍速/画中画** | ijk/DKVideoPlayer 原生支持 | 🔶 全屏已有；倍速/字幕/PIP 待查/待补 | ❌ |
| **广告过滤** | `AdBlocker` | ❌ | ❌ m3u8 广告段过滤 |
| **UA 欺骗库** | `assets/ua.db` | 🔶 `UA` 常量 | ⚪ |
| **备份/恢复** | `BackupDialog` | 🔶 仅 1 处命中 | ❌ 订阅+配置导出 |
| **XWalk 内核** | `XWalkInitDialog` + `XWalkUtils`（Chromium，用于网页解析） | 🔶 Windows 有 `WindowsWebSniffer`（WebView2） | ❌ Android 端无内核；Windows 端 WebView2 未接入爬虫解析 |

## 三、缺口优先级（按「能否解锁播放」排序）

### P0 — 直接决定「能不能放」的
1. **代理回调 `spider.proxy(Map)`**
   `SpiderProxyServer` 目前是宿主自实现。TVBox 语义是 `/proxy?do=…` → 交给爬虫自己的 `proxy()`（Android 侧 `JianpianP2P` 正是这么做的）。需解决 **`do` 参数 → 站点** 的路由。
2. **网页解析内核（WebView）**
   今天实测：荐片 19 条线路**全部**依赖 `Window.getDecorView()` 之后的 WebView 解析链；桌面 JVM 无浏览器引擎 → 挂死（现为 233s+）。
   → 方案：Windows 用 **WebView2**（项目已有 `WindowsWebSniffer` 可复用）做「取 DOM / 跑 JS / 嗅探 m3u8」回传爬虫；Android 用系统 WebView。
3. **补齐 Android 桩到「爬虫能跑完全流程」**
   见第五节清单（每补一个就往前推一层，方法已验证有效）。

### P1 — 体验完整度
4. **直播子系统**：m3u/txt 订阅解析、频道分组、EPG（可选）、直播代理（频道源常需换头）、带宽自适应、密码锁
5. **筛选器**（`GridFilterDialog` 对位）：分类 + 年份/地区/类型多维筛选
6. **播放器能力**：倍速、字幕（外挂/内嵌切换）、画中画/小窗、多内核（硬解↔软解切换）

### P2 — 生态与运维
7. 备份/恢复（订阅 + 配置 + 收藏导出）
8. 多订阅历史与快速切换
9. 投屏/推送（Cast/DLNA）、手机遥控（对齐 TVBox `RemoteDialog`）
10. 广告段过滤（`&ad=false` 落地）

### P3 — 锦上添花
11. 弹幕、UA 库、豆瓣详情、演员页、壁纸/主题

## 四、平台差异（重要）

| 能力 | Android | Windows |
|---|---|---|
| jar 爬虫 | ✅ dex 直载 | ✅ JVM 桥（**本项目独有**，TVBox 没有桌面端） |
| WebView 解析 | ✅ 系统 WebView | 🔶 WebView2（需接线） |
| 直播 | ✅ 可做 | ✅ 可做（无遥控器交互，改为鼠标/触屏） |
| 投屏/DLNA | ✅ 有意义 | ⚪ 桌面基本无意义 |
| 手机遥控 | ✅ 有意义 | ✅ 有意义（PC 当被遥控端） |
| 后台播放/通知栏/PIP | ✅ 有意义 | ⚪ 桌面意义有限 |
| 迅雷磁力 | ⚪ 需 native | ✅ **QEMU 方案已通**（本项目独有） |

## 五、今天实测暴露、可直接开工的清单

| # | 问题 | 证据 | 修法 |
|---|---|---|---|
| 1 | `AES/CBC/PKCS7Padding` 不可用 → 站点加解密失败 | `NoSuchAlgorithmException: Cannot find any provider supporting AES/CBC/PKCS7Padding` + `aes decrypt fail` | 标准 JVM 只有 `PKCS5Padding`。**注册一个极简 JCE Provider 把 PKCS7Padding 别名到 PKCS5Padding**（零外部依赖，~40 行），或引入 BouncyCastle |
| 2 | `Context.getContentResolver()` 缺失 → 「瓜子」整站挂 | `站点 瓜子 加载失败: NoSuchMethodError: 'android.content.ContentResolver android.content.Context.getContentResolver()'` | 补 `ContentResolver` 桩 + `Context.getContentResolver()` |
| 3 | `XPathGuard` 类找不到 → 「cc」整站挂 | `站点 cc 加载失败: ClassNotFoundException: XPathGuard` | 该类不在 jar 内，需查订阅里 cc 的真实 api 与 jar（可能指向另一个 jar） |
| 4 | `InitOrigin.context()` 注入时机 | 已修（提交 `db5be9f`） | — |
| 5 | `ActivityThread.i` / `WifiInfo.getIpAddress` / `getSystemService(wifi)` | 已修 | — |
| 6 | `PackageManager$NameNotFoundException` / `PackageInfo.signature` / `PreferenceManager` / `Activity.*` / `Window.getDecorView` | 已修 | — |
| 7 | 代理端口段 | 已修（多端口） | — |
| 8 | 搜索丢迟到结果 | 已修（两段式等待） | — |

## 六、建议的推进顺序

```
阶段 1（可立即开工，收益最大）
  ├─ P0-3 补桩（瓜子 ContentResolver、AES Provider 别名、cc 类名核对）
  ├─ P0-2 Windows WebView2 解析桥（复用 WindowsWebSniffer）
  └─ P0-1 /proxy → spider.proxy(Map) 回调（含 do→站点 路由）

阶段 2
  ├─ 播放器：倍速 / 字幕 / 小窗
  └─ 筛选器（GridFilterDialog 对位）

阶段 3
  └─ 直播子系统（订阅解析 → 频道组 → 直播代理 → 播放页）

阶段 4
  ├─ 备份/恢复、多订阅历史
  └─ 投屏 / 遥控 / 广告过滤 / 弹幕
```

## 七、复用资产（别重写）

- `TvBoxParseEngine` —— parse 语义已对齐，别再另起一套
- `WindowsWebSniffer`（WebView2）—— 是 P0-2 的现成底座
- `JianpianP2P`（Android）—— `/proxy` 回调爬虫的设计参考
- `SpiderProxyServer` —— 已是多端口 + m3u8 改写 + Range，只需加 `do=…` 分发
- `QemuThunder` / `BtStreamService` —— 磁力链路比 TVBox 强，不要动
- 测试装置：`_scratch_tb/{jpjprobe.py,pyproxy.py,proxytest,DecodeStrings.java,dump_strings.py}`
