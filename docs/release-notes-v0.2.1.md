猫爪影视 v0.2.1 —— 自 v0.1.4（2026-09-29）以来共 **192 个提交**，四条主线：

> **① 遥控投屏/推流链路**（宿主只做显示，真 Android 跑在 QEMU 里）打通并落地；
> **② x86 mini guest 转正为唯一运行时**，一堆 guest 侧顽疾根治（Go 代理崩溃链、proppreload CPU 100%、adbd/SF 双实例、持久盘）；
> **③ Windows 播放器与窗口一批修复**（libmpv 后端、21:9 裁剪、最大化抖动、全屏退出露标题栏、Esc 退不出）；
> **④ 虚拟机套件独立成 CatClaw.Qemu 仓库**，大二进制改走 Release 附件，主仓库瘦身。

安装包：`catclaw.video-0.2.1-Setup.exe`（**536.8MB**）。体积变化说明：v0.1.4 为 314MB，
本期 **+227MB** 是 x86 mini guest 的 Android 13 根镜像随包（旧 aarch64 合并 initrd 仅 164MB），
**−32MB** 来自 libmpv 退出安装包（见第五节）；净增约 223MB。

> **修订（首发包之后重新出包）**：首发包存在「打开网盘源『云盘配置』→ 点『登入自己云盘』必闪退」——
> 树渲染页在 WebView **构造期**就设 `HtmlWebViewSource`，MAUI 的 WebView2 代理在 `CoreWebView2` 尚未
> 初始化完成时回调 `LoadHtml` → 空引用（WinUI stowed exception `0xc000027b`，事件日志落在
> `Microsoft.UI.Xaml.dll`，应用自查日志 %TEMP%\catclawvideo_startup.log 有完整栈）。
> **现已修复**：① 构造期只建 WebView，HTML 缓存到控件 Loaded（Handler 就绪）后再设源；
> ② WinUI 异常钩子对这条已知栈标记已处理，宁可对话框空白也不让应用消失。本包为修复后的构建。

---

## 一、遥控投屏与推流（N1–N4）

- **流式中继**：网盘取流改流式转发，不再把整部片子读进宿主内存（883MB~2.35GB 的连续流原先必 OOM 且要等下完才起播）；**Range 定位**支持后网盘源真正可播（实测出画）。
- **流探测 + 一键投屏**端到端联调落地；`CatClawVideo.Stream` 与 RemoteView/RemoteStreamPrefs 就位（N4 验收）。
- 首页顶栏临时投屏入口撤除（能力保留在设置链路里）。

## 二、x86 mini guest（唯一运行时）与 guest 侧根治

- **Go 代理崩溃链修复**：binfmt 注册被 echo 转义坑 + NUL 截断毒 magic + vfork 改 fork + 哨兵带 pid/cmdline；直接解决「Go代理反复重启」。
- **proppreload 属性等待修复**：wait/wait_any 不再比较 serial（两套 prop_info 编号不通用，比较恒真 ⇒ libbase 纯用户态忙循环），adbd 不再恒定 100%；**qemu 空闲 CPU 562% → 14%**。
- **单实例守卫**：桥的 startAdbd（双实例抢 5555，输家空转 2 核并灌爆串口）、SurfaceFlinger（composer HAL 单客户端，重复拉起必崩）。
- **持久盘**：`e2fsck` 自愈脏 dentry、容量调大时无损扩容（不再删重建，**登录态保住**）、补 6 个内核模块。
- **磁力合并 VM**：qemu-user 转译 ARM harness 路线 + 同 VM 定案（磁力与爬虫桥共用一个 VM）。

## 三、Windows 播放器与窗口

- **libmpv 后端**（杜比视界 RPU tone-map）：DV 源强制软解（硬解 GPU surface 不带 RPU，会发绿发紫）、渲染闪退修复（BLOCK_FOR_TARGET_TIME 空指针 / FLIP_Y）、退出顺序竞态导致的闪退。
- **21:9 在 16:9 屏上被裁**：根因是夸克 mp4 容器声明比例错误、mpv 采信容器比例；配合窗口钳制与「原始比例」语义修正一并解决。
- **最大化窗口抖动**：全屏 presenter 跳过窗口钳制（全屏必大于工作区，钳制与 OS 全屏管理互相拉扯）+ 每秒兜底复查（覆盖任务视图/热插拔/DPI 切换）。
- **退出全屏露出系统标题栏**：`ExtendsContentIntoTitleBar` 需配合透明 TitleBar 背景色，退出后重新应用沉浸式框架。
- **Esc 退不出全屏** + 控件条自动隐藏策略（鼠标移入不再弹，单击切换）。
- 播放器高度跟随原生窗口；播放页全屏时内容宽度钉死页面宽（避免 2560 布局塞进 1920 窗口）。

## 四、虚拟机套件独立成库（三仓库协作）

- 宿主引擎（QEMU/ART/迅雷编排）迁出为 **CatClaw.Qemu** 类库：路径走 `QemuPaths.Configure`、结果走库内 DTO，应用侧用 `QemuMagnetEngine` 适配成 `IPreferredMagnetEngine`。
- Java 桥、guest 构建工具链、VM 文档一并迁出；**大二进制不再入库**，改由 Release 附件分发并由 `tools/fetch-assets.ps1` 按 SHA256 取件。
- 顺带修掉打包缺陷：`x86guest` 的镜像分片/备份（各 412MB）此前没被 csproj 排除，**每次 publish 白拷 1.2GB**。

## 五、启动体验与体积

- **启动画面重设计**：品牌 → 进度（转圈与百分比同行）→ 卡片式「启动自检」→ 底部通栏进度条。
- **启动环境自检**（Windows，7 项本地探测）：WHPX / QEMU 引擎 / 运行时镜像 / 数据目录 / 磁盘 / 内存 / 系统；异常项给**一键处置**（提权启用虚拟化并设 hypervisorlaunchtype、自动取件、打开数据目录）与「详细步骤」。
- **x86 运行时只认 WHPX**：不再回落软件模拟（实测 TCG 冷启动 ~3 分钟 vs WHPX ~11 秒，约 16 倍），无 WHPX 时直接判运行时不可用、磁力回落内置 BT。
- **修 ART VM 每分钟重启一次**（本版最重要的一处稳定性修复）：桥 ~0.5s 先用纯桥配置拉起 VM，磁力预热 ~3s 后才发现配置不对、推倒重建 —— 期间 jar/网盘源全程不可用，还会清掉 guest 里的网盘登录态。改为 VM 出生即带合并配置。
- **guest toast 去重**：同文案 60s 内只弹一次（网盘爬虫失败重试不再刷屏弹窗）。
- **libmpv 退出安装包**：判定为「装了也不能播、不值 115.4MB」→ 不再随包（安装包 −32MB、装完磁盘 −115MB）；代码侧加 `mpv-2.dll` 存在性守卫，缺件自动走 FFmpegInteropX/MF。要恢复取消 csproj 里那段 Content 的注释即可。
- 排除 Windows App SDK 连带带入的 AI 组件（onnxruntime/DirectML 40.3MB，`.iss` 早就排除过，本期同步到 publish）。

## 六、其它修复

- **首页**：冷启动失败不再永远卡在 97%；探测胜出写回首选（**回到上次的站点**）；首选站点预算与并发回归到实测稳定的取值。
- **jar 链路**：init 里 PORT 赋值重复导致桥进程秒退（jar 链路全断）修复；op=stackdump 自打线程栈。
- **spider-ui**：jar 的真实 View 树整树渲染、可点行内文字丢失、弹窗 4s 阻塞、纯展示框/按钮框并入树渲染页。
- **Android 目标**：js 流式中继的 `is` 模式对 `DexSpiderRuntime` 非法（既有缺陷）修复 —— Android 目标恢复可编译。
- 直播源设置页（地址/本地文件/历史 + EPG·UA·超时）；历史/收藏海报墙统一为首页布局。

## 七、已知问题

- 引擎首次就绪需 **30~60 秒**（Android 13 运行时启动开销）。要动得走 initrd 固化/磁盘根那条改造。
- 首页首次取数可能 **~34 秒**：110 个源站里实测只有约 8 个活着，死站各吃满 8s 超时。第二次启动因记住站点会明显更快。
- 安装包 536.8MB 主要来自 Android 13 根镜像（391MB，已 gzip），次为 QEMU 引擎与依赖（135MB，PE 导入表证实是加载期硬依赖）。
- 需要 WHPX（Windows 功能「虚拟机监控程序平台」）；未启用时启动自检会红字提示并可一键开启。

## 八、本轮修复（发布包重新构建）

**① 退出播放页后进程直接消失（最严重，已修）**
WinDbg 取证：崩溃桶 `STOWED_EXCEPTION_80004005_CoreMessagingXP.dll!DispatcherQueue::DeferInvokeCallback`，
原生栈 `KERNELBASE!RaiseFailFastException ← combase!RoFailFastWithErrorContextInternal2`；
托管侧完全无记录（该异常抛在我们代码之外的 WinUI 分发器里）。根因是**销毁顺序**：先
`MediaPlayer.Dispose()`、后由框架摘 `MediaPlayerElement` —— 元素在被摘掉前仍向 UI 线程
投递延迟回调，访问已销毁的播放器 → HRESULT 失败（E_FAIL/E_ABORT）→ fail-fast。
已改为：**先让元素离开可视树并净源，且退出时不再 Dispose 播放器**（留到下次建播放器时释放）。

**② 点「登入自己云盘」闪退 / 只出黑底（已修）**
三层叠加：① MAUI WebView 在构造期设 `HtmlWebViewSource` → WebView2 代理空引用崩进程；
② 装到 `C:\Program Files` 后 **WebView2 默认用户数据目录不可写** → 初始化失败
（这正是「Debug 正常、发行版闪退」的原因）；③ 换宿主时漏了把 HTML 交给它。
已修：全局 `WEBVIEW2_USER_DATA_FOLDER` 指向可写目录 + 树渲染改走**原生 WinUI WebView2 的正规 MAUI Handler**。

**③ 标题栏拖拽区反复重算（已修）**
`WindowDragHelper` 每次导航/尺寸变化都无条件 `ExtendsContentIntoTitleBar = true`，让 MAUI 的
`WindowRootView.UpdateTitleBarContentSize()` 抛 `E_INVALIDARG`（且抛在 WinRT 事件回调里）。
已改为仅在需要时设置，并对该已知栈标记为已处理兜底。同时**停用**了对已销毁元素做指针事件簿记的
「手动拖拽兜底」（本就是兜底，正常不触发）。

**④ 解析失败自动重试（新增）**
guest 里的桥崩过一次后，引擎重置、新桥其实是好的（用户实测「点第 2 集就正常」）。现在：异常带
「桥崩溃」→ 等 16s 自动重试一次；解析拿到不可播地址（如网盘复合 id）→ 等 3s 重试一次解析。

**⑤ guest 内存 2560 → 4096MB**（超大选集解析时的内存余量；实测 guest 内 `MemTotal 4008132 kB`）。

**已知未解**：guest 里的桥在解析几百集巨型选集后偶发 SIGSEGV（`[sig] s=11 a=0 … /memfd:jit-cache`，
即 JIT 代码内空指针）。网盘源受影响，重试可恢复；根治需改 guest 启动参数（如禁 JIT）并重打 initrd。
