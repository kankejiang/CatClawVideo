# 迅雷下载引擎 · Windows 宿主运行方案（qemu-system 路线）

目标：在 **CatClawVideo for Windows** 里跑起 **迅雷 P2SP 下载引擎**（`libxl_thunder_sdk.so`，AArch64 Android 原生库），
让磁力/种子能走迅雷的国内网络 —— **用户零安装**（不装 WSL、不装 qemu、不装 Java）。

## 为什么是 qemu-system 而不是 unidbg

| | unidbg | qemu-system |
|---|---|---|
| 引擎面对的环境 | 逐条模拟的 syscall + Java 桩 | **真实 Linux 内核** |
| 多线程 / TLS / socket | 模拟，实测 `createBtMagnetTask` 即崩 | 内核原生 |
| 结论 | 适合"跑一个函数"（如 Guard 解密），**不适合托管长期运行的多线程联网引擎** | ✅ 可用 |

unidbg 路线的代码保留在 `../unidbg-src/bridge/{ThunderRunner,ThunderJni}.java`（已能跑通版本号 API，但任务链撞墙）。

## 组成（全部可打包，用户零安装）

| 部件 | 来源 | 体积 |
|---|---|---|
| 内核 | Alpine `linux-virt` 的 `boot/vmlinuz-virt` | 9.2 MB |
| initramfs | cpio + xz(dict≤1MiB) + busybox-static + 引擎 + harness | **3.5 MB** |
| 引擎 | `libxl_thunder_sdk.so`(5.6MB) + `libxl_stat.so` | 6.4 MB |
| bionic | 设备 `/system/lib64` 的那套 + `linker64` | ~4.7 MB |
| QEMU | Windows 版 `qemu-system-aarch64.exe`（官方构建为静态单体） | ~35 MB |

## 结构

```
src/
  harness4.c        ← 主程序（由 gen_harness4.py 从 harness3.c 生成，单文件可编译）
  dnshook.c         ← DNS 拦截层（被 harness4.c #include）
  dnstest.c         ← bionic 网络自检小程序
  gen_harness4.py   ← 生成器：把上面这些补丁打进 harness4.c
build/
  build_initrd.sh   ← 打包 initramfs（WSL 侧执行，可用 MAGNET/FILENAME 环境变量注入任务）
  boot1.sh          ← 启动 qemu（含 filter-dump 抓包）
```

## 四个必须知道的坑（都踩过）

1. **initramfs 不能用 `xz -9`** —— 内核自带的 XZ 解码器不支持大字典，会
   `Initramfs unpacking failed` → `Kernel panic`。**必须 `xz --check=crc32 --lzma2=dict=1MiB`**。
2. **Alpine 内核把网卡驱动全编成模块**。要联网必须按序 insmod：
   `failover.ko` → `net_failover.ko` → `virtio_net.ko`（缺 `failover.ko` 会 `Unknown symbol failover_register`）。
3. **bionic 的 `getaddrinfo` 在无 netd 环境下彻底失效** —— 它走 `libnetd_client` → `/dev/socket/dnsproxyd`，
   而且**不读 `/etc/resolv.conf`**。现象是"裸 IP 能连、域名解析必失败（EAI_NODATA）"，
   引擎因此一个 socket 都建不起来。
   **解法见 `dnshook.c`**：在主程序里**重定义** `getaddrinfo` / `android_getaddrinfo` / `gethostbyname`
   （bionic linker 解析 dlopen 进来的 .so 的未定义符号时会先搜主可执行文件），
   自己发 UDP DNS 查询。**编译必须带 `-Wl,-export-dynamic`**。
4. **QEMU 缺 rom 数据目录**会直接失败（`failed to find romfile efi-virtio.rom`）：
   用 `-L <datadir>` 或系统安装版的 `/usr/share/qemu`，或 `-nic none`。

## 迷你 JNI 的关键约束

- 参数对象的字段**必须真的能读到**。`BtTaskStatus.mStatus` 是 `int[]`（构造器里 `new int[n]`），
  引擎 `GetObjectField(obj,"mStatus","[I")` **拿到 NULL 就直接判 `XL_PARAM_ERROR(9112)`**。
- `DvmObject` 不存 Java 字段值（unidbg 同理），别想反射它。
- 引擎会起线程，线程里要 `JavaVM` 的 `AttachCurrentThread/GetEnv` —— 必须给一个可用实现。

## init 的参数（全部从字节码反推）

```
XLLoader.init(soAppKey, "com.android.providers.downloads", appVersion, "", peerid, guid,
              statSavePath, statCfgSavePath, networkType, permissionLevel, queryConfOnInit)
```
- `soAppKey` = Base64("com.android.providers.downloads" + 0x00 + appId(LE 2B) + appType)
  推导链：`appKey.split("==")[0].replace('^','=')` → 去首尾各 2 字符 → Base64 解出 rawItems="6001"
  ⇒ `mAppId=6001`，`APPTYPE_PRODUCT=1`
  ⇒ `Y29tLmFuZHJvYWQucHJvdmlkZXJzLmRvd25sb2FkcwBxFwE=`（源码里的常量）
- **`networkType` 必须落在 `XLUtil.getNetworkType()` 的取值域**：Context 无效→0，**WiFi→9**，移动网络→5。
- `permissionLevel=1`、`queryConfOnInit=0`（以 `XLTaskHelper.init` 为准）

## 权威调用流程（从 SDK 字节码反出，别猜）

```java
// XLTaskHelper.init 之后还有三个调用
XLDownloadManager.setStatReportSwitch(false);
XLDownloadManager.setOSVersion(Build.VERSION.INCREMENTAL + "_alpha");
XLDownloadManager.setSpeedLimit(-1, -1);

// 磁力：XLTaskHelper.addMagentTask
int ret = createBtMagnetTask(url, filePath, fileName, GetTaskId);   // 3 串 + GetTaskId
if (ret == 9000) {                       // ★ 9000 = XL_NO_ERRNO = 成功
    long id = taskId.getTaskId();
    startTask(id);                       // ★ 必须显式启动
    setTaskGsState(id, 0, 2);
}

// 种子：XLTaskHelper.addTorrentTask
createBtTask(torrentPath, filePath, maxConcurrent=3, createMode=1, seqId, GetTaskId);
deselectBtSubTask(id, BtIndexSet);       // 反选不需要的文件（默认全选）
startTask(id); setTaskGsState(id, 0, 2);
```

## 错误码表（从 `XLDownloadManager.loadErrcodeString` 挖出）

```
9000 XL_NO_ERRNO(成功)   9101 XL_ALREADY_INIT     9102 XL_SDK_NOT_INIT
9103 XL_TASK_ALREADY_EXIST   9104 XL_TASK_NOT_EXIST   9105 XL_TASK_ALREADY_STOPPED
9106 XL_TASK_ALREADY_RUNNING 9107 XL_TASK_NOT_START   9108 XL_TASK_STILL_RUNNING
9109 XL_FILE_EXISTED     9110 XL_DISK_FULL         9111 XL_TOO_MUCH_TASK
9112 XL_PARAM_ERROR      9113 XL_SCHEMA_NOT_SUPPORT 9114 XL_DYNAMIC_PARAM_FAIL
9115 XL_CONTINUE_NO_NAME 9116 XL_APPNAME_APPKEY_ERROR 9117 XL_CREATE_THREAD_ERROR
9118 XL_TASK_FINISH       9119 XL_TASK_NOT_RUNNING  9120 XL_TASK_NOT_IDLE
9121 XL_TASK_TYPE_NOT_SUPPORT 9122 XL_ADD_RESOURCE_ERROR 9123 XL_TASK_LOADING_CFG
9301 XL_NO_ENOUGH_BUFFER  9302 XL_TORRENT_PARSE_ERROR 9303 XL_INDEX_NOT_READY
9304 XL_TORRENT_IMCOMPLETE 9900 DOWNLOAD_MANAGER_ERROR 9901 APPKEY_CHECKER_ERROR
114004 TASK_FAILURE_QUERY_BT_HUB_FAILED   ← 当前卡在这
```
`XLConstant$XLTaskStatus`: 0 IDLE / **1 RUNNING** / 2 SUCCESS / 3 FAILED / 4 STOPPED

## 引擎导出的 C API（`nm -D`，可直接 dlsym）

可用：`XLSetTaskAllowUseResource` / `XLSwitchOriginToAllResDownload` / `XLRequeryIndex` /
`XLEnterPrefetchMode` / `XLSetSpeedLimit` / `XLSetStatReportSwitch` / `XLStartTask` / `XLGetTaskInfo` …
⚠️ **`XLSetReleaseLog` / `XLIsLogTurnOn` 在 JNI 初始化路径下调用会崩**（SIGSEGV），不要用。
`XLSetReleaseLog` 的真实签名（反汇编）：`int (int enable, struct{const char*path; u32 pathSize,maxCount,maxSize;}*)`。

## 实测结论（2026-09-15）

✅ 已打通：
- 引擎在真内核里加载并 init 返回 **9000**
- **磁力 → 从迅雷 P2SP 索引服务真的下到了 .torrent**（`pool.bt.n0808.com:11400`，自定义二进制协议，
  `Host: res.res.res.res`，484 KB）并落盘
- **DHT 网络通**（`get_peers`/`find_node` 有真实节点返回）
- `createBtTask` 正确解析种子（`mFileSize` 与 ISO 真实大小一致）
- 引擎与 hub 服务器有真实 HTTP 交互（hub 回 200）

❌ 卡点：
- `createBtTask` 建的任务在 5 秒内变成 `st=3(FAILED) err=114004 (QUERY_BT_HUB_FAILED)`，
  即使已调用 `XLSetTaskAllowUseResource` / `XLSwitchOriginToAllResDownload` / `XLRequeryIndex` /
  `selectBtSubTask`，并已用 /etc/hosts 把迅雷已下线的 `btrouter.sandai.net`、
  `master.wap.dphub.sandai.net`（权威 DNS 返回 127.0.0.2）劫持到活着的 hub IP，仍然失败。

两个待验证的假设：
1. **内容相关**：迅雷资源池里没有该种子的 peer（测试用的是境外 Ubuntu ISO）→ 换成国内站点磁力即可
2. **引擎版本相关**：本引擎 `2.0.8.15-arm64_v8a`（SDK `6.0529.260.26`）偏旧，其 BT hub 路径已不被 2026 后端接受

## 复现步骤（WSL Debian）

```bash
# 一次性准备
sudo apt-get install -y qemu-system-arm          # 仅本机验证用
# 需在 ~/armrun 下放好：android/system/{lib64/*.so,bin/linker64}、vmlinuz-virt、*.ko、busybox.static

# 编译 harness（Windows 侧，用 NDK）
aarch64-linux-android21-clang harness4.c -o harness4 -ldl -Wl,-export-dynamic

# 打包 + 启动（MAGNET / FILENAME 可覆盖）
MAGNET='magnet:?xt=urn:btih:...' FILENAME=xxx.mkv bash build/boot1.sh 300 \
  -netdev user,id=n0 -device virtio-net-pci,netdev=n0
```
抓包文件在 `/tmp/guest.pcap`（`-object filter-dump` 导出），可用 `_scratch_tb/pcap_dump.py` 解析。


---

# 更新：直链 + 边下边播全链路打通（2026-09-15 深夜）

## 实测结果（用 https://releases.ubuntu.com/26.04.1/ubuntu-26.04.1-desktop-amd64.iso）

| 能力 | 实测 |
|---|---|
| 直链下载 | **7 ~ 17 MB/s**，其中 **P2S（迅雷服务端加速）占 6 ~ 17 MB/s** |
| CID/GCID | 引擎正确算出（mCid / mGcid 有值） |
| 本地播放服务 | `getLocalUrl` 返回 **9000 + 可用 URL**，`HTTP/1.1 206 Partial Content`（**支持 Range，可拖动进度**） |
| 实拉字节 | guest 内拉回 1024 字节，hex `000000206674797069736F6D…` = **MP4 的 `ftypisom` 头** ✅ |

## 五条必须记住的结论

1. **`getLocalUrl` 的参数是「绝对路径」，不是文件名。**
   （TVBox 官方写法：`getLoclUrl(cache + File.separator + mFileName)`）
   传裸文件名 → `9404`；传绝对路径 → `9000`。

2. **引擎的本地播放服务只监听 `127.0.0.1:<随机端口>`**（每次不同，如 33631 / 37087）。
   宿主侧 `hostfwd` 够不到（SLIRP 连的是 guest 的 10.0.2.15）。
   ⇒ **必须加一层 guest 内代理**：`0.0.0.0:<固定端口> → 127.0.0.1:<引擎端口>`，
   已实现 `proxy_loop` / `proxy_conn`（fork + select 双向转发），
   配静态 `hostfwd=tcp:127.0.0.1:20080-:20080` 即可从宿主拉流。

3. **非媒体文件拿不到播放地址**：`SHA256SUMS`（无扩展名）即使下载完成 `st=2`，
   `getLocalUrl` 仍返回 `9402`；换成 `.mp4` 立刻 `9000`。

4. **引擎返回的 URL 路径是「双重 URL 编码」的绝对路径**：
   `http://127.0.0.1:33631/%252Fthunder-data%252Fsample-5s.mp4`（`%252F` = 编码后的 `%2F`）。

5. **init 参数**（对照 TVBox 官方 `Thunder.java`）：
   - `appVersion = "21.01.07.800002"`（先前传 "1.0.0" 会被服务端当成不认识的旧客户端）
   - 需要设备指纹：`setImei` / `setMac`（TVBox 用随机值 + `XLUtil.isGetIMEI/isGetMAC = true`）

## 直链任务的权威调用（从 `XLDownloadManager.createP2spTask` 字节码反出）

```java
createP2spTask(mUrl, mRefUrl, mCookie, mUser, mPass, mFilePath, mFileName, mCreateMode, mSeqId, GetTaskId)
// 之后照旧 startTask → setTaskGsState(id, 0, 2) → 轮询 getTaskInfo
```

## 磁力路径的真相

`114004` 在 **TVBox 自己的 `errorInfo()` 映射**里 = **"版权限制：无权下载"**
（不是按引擎内部枚举名理解的 "QUERY_BT_HUB_FAILED"）。

同一份内容对照：**直链 7~17MB/s 成功，磁力 `st=3 err=114004` 失败**
⇒ 迅雷对 **BT 通道**有策略限制，对**直链通道**正常加速。**不是本机环境的问题。**

待验证：用一条**真实的国内磁力**确认 114004 是"内容相关"还是"BT 通道整体受限"。


---

# 🎉 全链路打通（Windows 原生，2026-09-15 收尾）

## 最终结果：宿主真的把视频流拉下来了

| 请求 | 结果 |
|---|---|
| 无 Range | `HTTP=200`，2848208 字节（整个文件），0.33s |
| 带 Range `0-1023` | `HTTP=206`，1024 字节 |
| 前 16 字节 | `00 00 00 20 66 74 79 70 69 73 6f 6d …` = MP4 的 `ftypisom` 头 ✅ |

链路：**宿主 → qemu hostfwd(127.0.0.1:20085) → guest 代理(0.0.0.0:20080)
→ 重新 getLocalUrl → 迅雷引擎本地服务(127.0.0.1:随机端口) → 真实字节**

## 最后一跳的三个真正原因（都踩过，务必记住）

1. **链尾不能调 `unInit`**
   它是 SDK 的清理入口，调完之后引擎变成「未初始化」，后续任何调用
   （包括为播放器重新取播放地址）都会返回 **9102 = XL_SDK_NOT_INIT**。
   **引擎要长期运行，产品里永远不调它。**

2. **引擎状态是「线程相关」的**
   在代理线程里调 `getLocalUrl` 同样得到 9102。
   解法：代理线程发请求 + volatile 变量握手 + **主线程**执行 `getLocalUrl`。

3. **引擎的本地播放服务是「一次性」的**
   一个客户端断开后就不再接受新连接（第二次连它 = ECONNREFUSED）。
   **每次播放器连进来都要重新调一次 `getLocalUrl`**，
   并用返回 URL 里的**新端口**去连（端口每次都变）。

4. 代理必须监听 `0.0.0.0`（引擎只听 `127.0.0.1`，宿主 hostfwd 够不到 loopback）。
   qemu 的 hostfwd 端口被占用时会**静默拒绝启动**，
   只报 `Could not set up host forwarding rule`，换端口即可。

## Windows 原生启动命令（已实测可用）

```
qw64\qemu-system-aarch64.exe -M virt -cpu max -m 2048 -smp 4 -nographic -L qw64/share ^
  -kernel pkg_kernel -initrd pkg_initrd.xz ^
  -append "console=ttyAMA0 rdinit=/init loglevel=4" ^
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:20085-:20080 ^
  -device virtio-net-pci,netdev=n0
```

## 打包体积（实测）

| 部件 | 未压缩 | 压缩后 |
|---|---|---|
| QEMU（exe 30.9MB + 104 个 DLL） | 127 MB | **30.2 MB** |
| 内核 `vmlinuz-virt` | 9.2 MB | ~9 MB |
| initramfs（busybox+bionic+引擎+harness） | 13 MB | 3.5 MB |
| **安装包增量** | | **≈ 43 MB** |

---

# 产品化第一步：运行时控制通道打通（2026-09-15 深夜）

## 关键突破：任务不再是「编译期烧进 initramfs」，而是**运行时由宿主下发**

```
宿主控制端 (127.0.0.1:18080)                guest harness
   │  GET /task   ←──────────────────────   每秒轮询（走 qemu 用户网络的 10.0.2.2）
   │  ──────────→   TASK URL <url> <name>
   │                                         → 迅雷引擎建任务 / 启动 / 监控
   │  GET /report?ev=play&...   ←──────────  状态、播放地址回帖
   │
   └─ 播放器播 http://127.0.0.1:<hostfwd>/<path> → hostfwd → guest 代理 → 引擎
```

### 实测（Windows 原生，全程无人干预）

```
宿主下发: TASK URL https://download.samplelib.com/mp4/sample-5s.mp4 sample-5s.mp4
guest:    [ctrl] ✅ 播放地址 = http://127.0.0.1:39293/%252Fthunder-data%252Fsample-5s.mp4
宿主收报: [宿主] ↑ play id=1002 st=2 err=0 2848208/2848208 /%252Fthunder-data%252Fsample-5s.mp4
宿主拉流: 无Range → HTTP=200 2848208 字节； 带Range → HTTP=206 262144 字节
          前 16 字节 = 00 00 00 20 66 74 79 70 69 73 6f 6d …（MP4 的 ftypisom 头）
```

### 协议（纯文本，详见 `src/ctrlloop.c` 头部注释）

- 宿主 → guest（`GET /task` 的响应体）
  `NONE` / `PING` / `TASK MAGNET <uri> <name>` / `TASK URL <url> <name>` / `STOP`
- guest → 宿主（`GET /report?...`）
  `ready` / `started` / `status` / `play` / `error` / `pong`

### 又踩到的三个点

1. **代理要无条件启动**：空任务链下 `g_engine_port` 是 0，
   先前「端口 > 0 才起代理」导致代理根本没起。
   端口应当**每次连接时由 rearm 动态给出**。
2. **JNI 上下文（env / thiz / localUrl）要在链尾无条件设置**，不能只在某个阶段里设。
3. **控制端任务只发一次**：测试脚本每轮要重开控制端，否则 guest 拿到的是 `NONE`。

### 写进 CatClawVideo（Windows 端）还差的事

1. 把 `qw64/`（qemu + DLL）、`pkg_kernel`、`pkg_initrd.xz` 打进安装包（增量 ≈43 MB）
2. MAUI 侧：起 qemu 子进程 + 内置控制端（把 `ctrlserver.py` 的逻辑用 C# 写，
   或直接起一个 HttpListener）+ 解析上报 + 播放器播 `http://127.0.0.1:<portfwd>/<path>`
3. 磁力支线：`114004`（TVBox 官方映射 =「版权限制：无权下载」）**仍需一条国内磁力来定性**

---

# 磁力全链路推进 + 114004 定性（2026-09-15 深夜，第二轮）

## 起因：手机能播，PC 为什么不能

用户反馈「手机端磁力都能播放」。核对手机（com.catclaw.video.debug）的 `files/logs/bt.log`：

```
[12:23:12.898] [迅雷] 文件列表 2 项            ← 1.2 秒出种子文件列表
[12:23:14.258] [迅雷] 起播 index=0
[12:23:14.264] [bt] 优先引擎（迅雷）命中：…909.9MB
```
`[迅雷]` 在日志里出现 44 次、全部成功；00:20~00:21 一分钟内连开 4 条磁力（6/1/4/6 项文件列表）秒出。
⇒ **迅雷 BT hub 对这个客户端是开放的**，此前「迅雷对 BT 通道有策略限制」的结论**作废**。

## 用手机的真实磁力做对照实验

手机缓存里每个种子目录都藏着一个以 infohash 命名的隐藏文件：

```
cache/thunder/01-02.1080p.HD中字/.1F8E2B67E3142CD3B0B26128C6D2FFB8C33C00DB
cache/thunder/01.1080p.HD国语中字无水印[...]/.1363FB911E8603FDE757C9B02979D508DE2D195B
```
取出头部即可读回 infohash（**这是取「真实国内磁力」的可靠办法**，不必去站点抓）。

## 新增能力：运行时下发磁力 → 自动展开 → 指定文件起下载

控制通道补了两条（`src/ctrlloop.c`）：

| 命令 | 作用 |
|---|---|
| `TASK MAGNET <magnet> <name>` | 只把磁力解析成 `.torrent` 并落盘（不再当下载任务） |
| `DL <torrent>\|<dir>\|<relPath>\|<index>\|<exclude-csv>` | 起真正的 BT 下载任务（种子可传 `-` 表示用刚下发那条磁力的种子） |

宿主侧 `build/ctrlserver2.py` 是**完整编排器**：下发磁力 → 经 guest 代理把 `.torrent` 拉回来 →
bencode 展开文件列表 → 挑最大视频 → 下发 DL → 等播放地址 → 拉 256KB 验证。

`start_dl()` 严格对齐 **手机 `XLTaskHelper.addTorrentTask` 的反编译结果**
（`javap -c` thunder.jar 得到），三处关键差异都补上了：

1. **先 `getTorrentInfo(torrentPath, ti)`** —— 引擎自己解析种子建内部索引（少了它 `setTaskGsState` 回 9303 INDEX_NOT_READY）
2. **多文件用 `deselectBtSubTask`（反选不要的），不是 `selectBtSubTask`（正选要的）**
3. **`setTaskGsState(id, 被选中文件的 index, 2)`** —— 第二参是索引，不是 0
（另：`seqId` 用递增计数，不是固定 1）

## 实测结果

| 环节 | 结果 |
|---|---|
| 磁力 → `.torrent` 落盘 | ✅ 2946 字节，bencode 合法，`length=954147489`（909.9MB，与手机日志一致） |
| 宿主展开文件列表 | ✅ 2 项，选中 #1（1043.6MB） |
| `getTorrentInfo` / `createBtTask` / `deselectBtSubTask` / `startTask` | ✅ 全 9000，任务 `total` 正确收敛为**只含选中文件**（1094334176） |
| **BT 下载（hub 查询）** | ❌ `st=3 err=114004`，**国内外磁力都一样** |

⇒ **114004 与内容无关**（境外 ISO、国内 dyyg7 磁力表现一致），是**环境/引擎侧**的问题。

## 抓包定位（`hub.pcap` + `hub_flows.py`）

| 流 | 结果 |
|---|---|
| → `112.64.218.66:11400`（种子索引 pool.bt.n0808.com） | **200 OK，484 KB** —— 种子就是这样下到的 ✅ |
| → `116.132.223.136:80`（idx/hub5btmain） | 200 OK，但响应体仅 80 字节 |
| → `112.64.218.40:80`（被 `/etc/hosts` 劫持的 btrouter/master.wap.dphub） | **400 Bad Request**（nginx） |

**最关键的发现**：手机 `files/setting.cfg`（base64，引擎写的**服务器下发运行配置**）：

```json
"server": { "phub_host" : "pr.m.hub.sandai.net",
            "int32_server_min_pipe_count" : 35, "server_max_pipe_count" : 45 },
"P2P":    { "max_phub_pipe_count" : 150 },
"strategy": "(scdn.closeScdn)(server.resStrategy_20210727)(P2P.p2pPipeCount_20210727)…"
```

而 **VM 里这份配置是空的**，抓包里 `phub_host` / `pr.m.hub` / `rp.m.hub` **命中 0 次** ——
引擎从没拉到过配置，P2P hub 主机名无从得知，只能退回 `.so` 内建的旧域名（已下线/被沉）。

已试过但**无效**的手段：`QCO=1`（queryConfOnInit）、把劫持目标从 BT 主 hub(.40) 换成手机配置里的
`pr.m.hub.sandai.net`(.71)。⇒ 配置拉取不是这个开关能触发的，链路更深。

## 当前结论（写在播放器里之前必须知道）

- PC（VM）侧：**磁力解析通道已完全打通**（能拿到种子、能选文件、能建任务），**只差 BT hub 查询这一步**。
- 这一步依赖引擎的服务器下发配置，而 VM 环境里拿不到 ⇒ 需要继续挖配置拉取路径，
  或改走**不依赖 BT hub 的路线**（见下）。
- 手机端一切正常，可作为**短期兜底**（把手机的迅雷引擎经局域网借给 PC，与既有「解析节点」同构）。

## 待办

1. 挖「服务器配置从哪个 URL 拉」——线索：`flowcontroll.dcdn.sandai.net:8080/query`（抓包里唯一有配置味的 200 响应，448 字节）
2. 或改走：**迅雷网盘云添加**（`ThunderPanEngine`，PC 原生、官方接口，需登录）
3. 或短期：**手机借力**（局域网节点转发手机的迅雷播放地址）

---

# 🎉 114004 破案：引擎身份签名（2026-09-16 凌晨）

## 结论：114004 不是「版权限制」，是**身份签名不对**导致的 hub 请求被拒

前一轮把 114004 归因为「缺服务器配置（phub_host）」，**不完全对**。真正让它消失的是
**把引擎的身份对齐到手机那一套**。

## 破案链条

1. **`XLTaskHelper.addTorrentTask` 之后仍读诊断字段**（新增 `diag` 打印）：
   ```
   cid=  gcid=  queryIdx=1 → 3        ← 索引查询失败，CID/GCID 都没算出来
   ```
   而直链任务的 cid/gcid 是有值的。

2. **抓包看 hub 交互**：引擎向 hub 发的请求体是**加密签名块**（156 字节，头
   `01 00 00 00 01 00 00 00 90 00 00 00` + 144 字节密文），服务器回
   **400 / 500**（换台服务器：`400 Bad Request` / `500 Internal Server Error`）。

3. **从 `thunder.jar` 反编译出身份来源**：
   - `XLUtil.getPeerid()`：**peerid = `MAC` + `"004V"`**（其次 `IMEI` + `"V"`，或读身份文件）
     —— 我们先前用的是「36 位随机 hex + 004V」，**格式就不对**
   - `XLDownloadManager.setOSVersion(s)` → 实际调 `XLLoader.setMiUiVersion(s)`，
     Java 层传的是 `Build.VERSION.INCREMENTAL + "_alpha"`
   - `XLDownloadManager.init()` 之后调 `setLocalProperty("PhoneModel", Build.MODEL)`
   - 引擎自己维护身份缓存文件 **`<mStatSavePath>/Identify2.txt`**，格式：
     ```
     peerid=<MAC>004V
     MAC=<12位十六进制>
     IMEI=<15位>
     ```

4. **对齐实现**（`src/harness4.c`）：
   - `setImei` / `setMac` **挪到 init 之前**（放到之后会返回 9102 SDK_NOT_INIT）
   - `peerid = <MAC>004V`（真机值 `FA25CC5B3363004V`）
   - init 之后补 `setMiUiVersion("OS2.0.6.0.UKBCNXM_alpha")` + `setLocalProperty("PhoneModel","M2011K2C")`
   - initrd 预置 `Identify2.txt`（61 字节，与手机一致）+ `setting.cfg`（1020 字节，含 phub_host）

## 实测：114004 消失 ✅

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 任务状态 | `st=3 err=114004`（5 秒内判死） | **`st=1 err=0` 运行中** |
| `queryIdx` | 1 → **3**（失败） | 1 → **2** |
| `cid`/`gcid` | 空字符串 | `0000…0`（有槽位了） |
| BT hub 响应 | 400 / 500 | **200 OK + 10604 字节**（有数据了） |
| 播放地址 | 无（任务已失败） | **引擎给出所选文件的播放地址**（id=1003） |

```
[chain]   setMiUiVersion(OS2.0.6.0.UKBCNXM_alpha) → 9000
[chain]   setLocalProperty(PhoneModel,M2011K2C) → 9000
[ctrl] 建下载任务(BT) 返回 9000，id=1003 seq=1
[ctrl] t=… st=1 err=0 已下载=0/1094334176 速度=0
[ctrl]   diag cid=000…0 gcid=000…0 queryIdx=2 infoLen=0 附加源=2
```

## 仍差最后一步：数据没起量

`已下载=0 / 速度=0` —— 任务活着、资源索引拿到了 10.6KB，但引擎还没开始拉字节。
下一步排查方向（按优先级）：

1. **hub 响应内容**：那 10604 字节是「资源列表」还是「无源」？需要解密/比对协议
2. **资源挂载 API**：`btAddServerResource` / `btAddPeerResource` / `addPeerResource`
   （jar 里的 XLTaskHelper 不调它们，但 TVBox 某些分支会）
3. **`XLEnterPrefetchMode` / `XLRequeryIndex`**：任务健康时踢一脚试试（harness 里已有 dlsym）
4. **`setUserId`**：引擎可能要求账号态才给 P2SP 服务端加速
5. **对比手机侧**：真机同一磁力 `已下载` 是否立刻增长（手机日志 `[迅雷] 起播 index=0` 后应有时长）

## 环境备忘（本轮新增）

- 诊断字段打印在 `ctrlloop.c` 的 `poll_task()`：`cid/gcid/queryIdx/infoLen/附加源`
- 抓包分析：`build/hub_flows.py`（按迅雷网段筛选四元组）+ `build/dump_flows.py`（按目的 dump 完整载荷）
- `build/build_initrd.sh` 现在会预置 `setting.cfg` + `Identify2.txt` 到 `/thunder-data/`
- **setImei/setMac 必须在 init 之前调用**


---

# TVBox 源码对照 + 又排除 4 项（2026-09-16 凌晨，提交 6b4fd9d）

## 参考实现在本地

`D:/Code/sourceCode-ccc25f6/app/src/main/java/com/github/tvbox/osc/util/thunder/Thunder.java`

**逐行比对结论：TVBox 的磁力流程与本项目实现完全一致**

```
addMagentTask(magnet, cacheRoot, getFileName(magnet))
  -> 轮询 getTaskInfo 直到 mTaskStatus == 2（= 种子解析完成）
  -> getTorrentInfo(cache路径) 取 mSubFileInfo
  -> addTorrentTask(种子, cacheRoot/<种子名去扩展名>, mFileIndex)
  -> 轮询 getBtSubTaskInfo(tid, index) 到 1/4/2
  -> getLoclUrl(cache + "/" + info.mFileName)
```

⚠️ 另：TVBox 的 `errorInfo()` 把 **114001 / 114004~114007 / 114011 / 9304 / 111154 统一映射成
「版权限制：无权下载」** —— 那是一大类错误的公共文案，**不能按字面理解**（本项目的 114004
已证明是身份签名问题）。

## 本轮修 1 个真 bug + 排除 3 个假设

| # | 项 | 结果 |
|---|---|---|
| 1 | **JNI 垫片缺口**：`NewObjectArray`(#172) / `SetObjectArrayElement`(#174) / `SetBooleanField`(#105) 是未实现陷阱 → **引擎 `getTorrentInfo` 解析出的文件列表被整包丢弃** | ✅ 已实现（含 `JObjArray` 注册表），日志现已能看到 `mIsMultiFiles=true` + 2 个文件 + `mIsSelect` |
| 2 | 磁盘空间：initramfs rootfs 是 **ramfs**，statvfs 空闲上报不可信，BT 要预分配整个文件 | ❌ 改挂**真 tmpfs**（1500m）+ 内存 4096，仍零字节 |
| 3 | 网络状态通知：`notifyNetWorkType(9)` / `setNotifyWifiBSSID(...)` / `setNotifyNetWorkCarrier(0)` | ❌ 全部返回 9000，仍零字节 |
| 4 | `XYVodSDK_*`（VOD/P2P 数据面组件）未启用 | ❌ `getSdkEnabled()=1` 本来就启用着，仍零字节 |

## 累计已排除（11 项）

内容(国内外) · 身份签名(已修) · 服务器配置 · 网络通知 · 下载目录名 · 种子索引 ·
文件列表回填 · 磁盘空间 · 预取模式 · 子任务轮询 · VodSDK

## 现状

**唯一剩下的差异：真机 Android 运行时 vs bionic 垫片** —— 数据面可能存在垫片未覆盖的依赖。
每轮验证成本 = 编译 + 打 initrd + 起 VM ≈ 3 分钟，但方向已不明确，属盲试。

---

# 🎉🎉 磁力边下边播在 Windows 上完全跑通（2026-09-16 凌晨）

## 结果

```
[编排] ⑤ 从代理拉前 256KB 验证
[编排]     HTTP=206 字节=262144 前16字节=000000186674797069736f6d00000001
[编排]     🎉 磁力边下边播已验证通过
```

拉到的字节解码：`....ftypisom....isomavc1..6.moov` = **真 MP4**。
下载过程：`已下载=478152857/1094334176 速度=2534805`（**P2P 通道 2.5 MB/s 持续**）。

**全链路（全自动、无人干预）**：
宿主下发磁力 → guest 解析成 `.torrent` → 宿主经代理取回并用 bencode 展开文件列表 →
下发选片命令 → guest 建 BT 任务 → P2P 下载 → 引擎给本地播放地址 → 宿主 HTTP 206 拉流。

## 让零字节消失的关键改动（A/B 实证：只有一个）

对比上一轮（同样的磁力、同样的身份，却零字节），本轮改了两处；随后补做 2×2 A/B 实验，**证明只有一处是必要的**：

| 实验 | 4 个多余调用 | 磁力带 `dn=` | 结果 |
|---|---|---|---|
| mag22–25（4 次） | 发 | 否 | 0 字节 |
| mag26 | **不发** | 是 | ✅ 完整下载 1.09 GB（P2P 2.4–2.8 MB/s，`st=2` 全程完成） |
| mag27 | 发 | 是 | 0 字节（4 个调用确已发出，返回 9000/9000/9106/9000） |
| mag28 | **不发** | **否** | ✅ 裸 `btih` 同样跑满速（亦完整下载 1.09 GB，`st=2`） |

**结论：跑通的关键 = 不发那 4 个多余调用；磁力带不带 `dn=` 无关（原推断作废）。** 现场日志：`_scratch_tb/证据-mag2*.log`。

### 1. ★ 唯一关键：去掉「我们自作聪明加的」4 个多余引擎调用

手机端 `thunder.jar` 的 `XLTaskHelper` 只调这四步：

```
createBtTask(param) → deselectBtSubTask(未选中的) → startTask → setTaskGsState(id, index, 2)
```

**它从来不调** `XLSetTaskAllowUseResource` / `XLSwitchOriginToAllResDownload` /
`XLEnterPrefetchMode` / `XLRequeryIndex` —— 这几个是早期「救火」时加的，**A/B 实锤：发了就零速度**。
现在它们被收到 `EXTRA=1` 开关后面，默认不发。主犯疑似 `XLSwitchOriginToAllResDownload`：
把任务切到「资源只走 origin」模式，BT 没有 origin → 四通道全 0。

### 2. 磁力原样透传（保留为实践建议；A/B 已证非必要）

手机侧 stat 记录（`statstorage_v5.xml` 解出）显示站点播放链接是完整磁力（含 `dn=`）：
`Url=magnet:?xt=urn:btih:1f8e2b67…&dn=中头奖还是要上班…`

当时推断「必须照抄、不能自己拼裸 btih」；**mag28 实验（裸 btih、其余相同）照样跑满速，推断作废。**
仍建议原样透传（零成本、保留显示名），但它**不是**跑通前提。

## 这一路修掉的东西（按发现顺序）

| # | 问题 | 修法 |
|---|---|---|
| 1 | 引擎身份签名不对 → hub 回 400/500 | `peerid = MAC + "004V"`；`guid = IMEI + "_" + MAC`；`setImei/setMac` 挪到 init **之前**；补 `setMiUiVersion("<incremental>_alpha")`、`setLocalProperty("PhoneModel", …)`；预置 `Identify2.txt` |
| 2 | 引擎缺服务器配置 | 预置手机的 `setting.cfg`（`phub_host` / 策略串） |
| 3 | 网络状态没告诉引擎 | `notifyNetWorkType(9=WIFI)` / `setNotifyWifiBSSID(...)` / `setNotifyNetWorkCarrier(0)` |
| 4 | JNI 垫片把引擎回填的文件列表丢了 | 实现 `NewObjectArray` / `SetObjectArrayElement` / `SetBooleanField` |
| 5 | 下载目录名带空格超长 + 目录不存在 | 用 TASK 行里的种子名当目录（= 手机的做法）+ 先 `mkdir` |
| 6 | **VOD/P2P 数据面没起来** | **`XYVodSDK_initUnixSock` 的参数是「目录」**（引擎在它下面建 `<随机>.socket`）→ 先 `mkdir /thunder-data/vod.sock`，返回值从 **-102 → 0**，引擎日志随即出现 P2P 栈（`natdetection.onethingpcs.com`、`get scid/scg`） |
| 7 | ramfs 空间上报不可信 | `/thunder-data` 改挂真 **tmpfs**（1500m）；QEMU 内存 4096 |

## 引擎自己的日志（重要发现）

**引擎把内部日志打在 stdout 上**，格式 `[YYYY-MM-DD HH:MM:SS][LEVEL]msg`，
QEMU 控制台日志里直接能抓到。关键行：

```
[DEBUG]get scid 61dd7971-…            ← 会话 id
[DEBUG]request configure json
[ERROR]parse json failed.             ← 配置拉取失败（见下）
[DEBUG]dns cache set [natdetection.onethingpcs.com], ip […]
[DEBUG]detect nat type 4              ← NAT 类型（SLIRP = 对称型）
[WARN]listen unix, unix_path:/thunder-data/vod.sock/….socket   ← vod.sock 修好后才有
```

**配置服务器的现状（不影响可用性）**：
`conf-darwin.xycdn.com/psdk_param?version=2.0.8.15` → 一律 **404**（带不带 Scid 都一样）；
`sdk1xyajs.data.p2cdn.com` → **NXDOMAIN**。
所以引擎退回「sdk default configure」——**手机也是这样，照样能播**，不用管。

⚠️ `XLSetReleaseLog` / `XLIsLogTurnOn` 在本环境调用**必崩**（SIGSEGV），别调。

## 复现命令

```bash
# ① 编译 harness（NDK）
cd /d/Code/_scratch_tb/thunder-harness
NDK="C:/Users/lvjin/AppData/Local/Android/Sdk/ndk/27.0.12077973/toolchains/llvm/prebuilt/windows-x86_64/bin"
"$NDK/aarch64-linux-android21-clang.cmd" harness4.c -o harness4 -ldl -Wl,-export-dynamic

# ② 打包 initrd（WSL；会预置 setting.cfg + Identify2.txt，挂 tmpfs）
wsl -d Debian bash -c 'cd /mnt/d/Code/_scratch_tb/qemu-system && MAGNET= URL= MON_SECS=0 \
  PROXY_PORT=20080 CTRL_PORT=18080 bash build_initrd.sh >/dev/null 2>&1; \
  cp ~/armrun/initrd.xz /mnt/d/Code/_scratch_tb/qemu-system/pkg_initrd.xz'

# ③ 起宿主控制端（任务行 = 站点原样给的磁力；dn= 可选，A/B 已证无关）
python ctrlserver2.py "TASK MAGNET magnet:?xt=urn:btih:<HASH>&dn=<URL编码的片名> name.mp4" 18080 20092 &

# ④ 起 VM（内存 4096，媒体口 hostfwd）
./qw64/qemu-system-aarch64.exe -M virt -cpu max -m 4096 -smp 4 -nographic -L qw64/share \
  -kernel pkg_kernel -initrd pkg_initrd.xz -append "console=ttyAMA0 rdinit=/init loglevel=4" \
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:20092-:20080 -device virtio-net-pci,netdev=n0 &
```

## C# 集成（Windows App，2026-09-16 已落地）

宿主编排从 `ctrlserver2.py` **逐行移植为 C#**，随应用分发，运行期不再需要 Python/Git-Bash：

| 部件 | 位置 |
|---|---|
| 引擎（`IPreferredMagnetEngine` 实现） | `CatClawVideo.Core/Services/QemuThunder/QemuThunderEngine.cs` |
| 控制端（≡ ctrlserver2.py 的 HTTP 服务） | 同目录 `QemuControlServer.cs`（裸 `TcpListener`，避 http.sys 的 URL ACL） |
| QEMU 进程管理 | 同目录 `QemuHostRuntime.cs`（Job Object 防孤儿进程、stdout 走日志并过滤 jni 噪声） |
| 种子解析（bencode） | 同目录 `Bencode.cs` |
| 引擎链（QEMU → 网盘兜底 → 内置 BT） | `CatClawVideo.Core/Providers/ChainedMagnetEngine.cs` |
| 运行时（142MB；图形栈已裁） | `CatClawVideo.Maui/ThunderRuntime/`（csproj Windows 条件分发，溯源见 PROVENANCE.md） |
| 注册 | `MauiProgram.cs` 的 Windows 分支 |

**验收（2026-09-16 05:05）**：C# 宿主全链路 ≡ 实验装置，两轮全通过
（33.7s / 21s：QEMU 启动 → 磁力 → 文件列表 → DL → 播放地址 → HTTP 206 + 真 MP4 头 262144 字节）。

**控制台回归测试**（改宿主后必跑）：

```bash
cd CatClawVideo/JavaBridge/qemu-src/hosttest
dotnet run -c Release -- "<runtimeDir>" "magnet:?xt=urn:btih:<HASH>" [preferName]
# runtimeDir 例：D:\Code\_scratch_tb\qemu-runtime（= ThunderRuntime 的源目录）
# 退出码 0 = 全链路通过
```

**行为细节**（对齐 Python 版，另有增强）：

- 控制口 `18080` 烧死在 initrd（改不了）；媒体口从 `20092` 起、被占自动向后探测空闲端口
- VM **懒启动**（首次任务时才起）+ 常驻复用 + **空闲 15 分钟自停**；单 VM 单会话（新任务会断开旧流）
- 拉流验证：≥64KB（HTTP 200/206）判成功；60s 内 0 字节判失败 → 链式回落（网盘引擎 → 内置 BT）
- 阶段一取种子优先用 guest 的 play URL 路径；拿不到时用「原路径合成双重编码」兜底
- 运行时长日志：`%LOCALAPPDATA%\CatClawVideo\qemu-console.log`（含被过滤的 jni 全量输出）

**开发提醒**（照抄调用顺序，别加料）：

1. 磁力任务只走四步：`createBtTask → deselect → start → setTaskGsState`；**绝不调**
   `XLSetTaskAllowUseResource` / `XLSwitchOriginToAllResDownload` / `XLEnterPrefetchMode` /
   `XLRequeryIndex`（A/B 实锤：发了就零速度）
2. `XLSetReleaseLog` / `XLIsLogTurnOn` 在本环境**必崩**（SIGSEGV），别调
3. 磁力原样透传即可（`dn=` 可有可无）；下载目录名用「TASK 行给的名字去扩展名」，先 mkdir

## 播放卡顿修复：宿主读前缓存代理（2026-09-16）

**实测到的三个结构性代价**（`hosttest bench` + 原始 socket 基准）：

| 症状 | 实测 | 后果 |
|---|---|---|
| 每连接一次 guest 重武装 | ~210ms/连接（B0 微请求中位） | 播放器分块/重连时不断出现静默空隙 |
| guest 代理 accept **串行** | 第二条连接被完全饿死（12s 零字节） | 播放器一旦并行连接就卡死 |
| 小分块读被连接开销拖垮 | 1MB/块中位 229ms（≈4.4MB/s）；64KB/块 <0.5MB/s | 有效吞吐低于码率 → 断流 |

**修复**：`CatClawVideo.Core/Services/QemuThunder/QemuStreamProxy.cs` —— 宿主侧读前缓存代理：

- 对**播放器**：普通本地 HTTP（Range / keep-alive / 任意并发连接随便用），数据从内存缓存出；
- 对**上游**：只用一条长连接按 32MB 切片顺序续拉（吸收 210ms 重武装开销），
  窗口随最慢读者滑动、上限 64MB（保留 8MB 回退余量）；
- 播放器请求出窗（跳 moov 尾 / 后退 seek）→ 一次「重定位」：清窗、上游改从该偏移拉；
- 代理起不来时静默回落直连媒体口（功能不受影响）。引擎返回给播放器的地址已自动换成代理口。

**修复后同场实测**：

| 指标 | 直连媒体口 | 走缓存代理 |
|---|---|---|
| B0 连接开销 | ~210ms | **0ms** |
| B3 1MB 分块读 | 中位 229ms | **2ms** |
| 并发第二连接 | 被饿死 | **正常应答** |
| 缓冲命中吞吐 | 14MB/s（链路） | **≥330MB/s**（内存） |

**天花板提醒**：代理只解决「交付平滑度」，解决不了「上游速度」——播放均速仍受引擎下载速度限制
（实测 2.5–4.8MB/s）。1080p 常规码率无压力；2160p 高码率片源若下载速度不够，仍会周期性缓冲（这是物理限制）。

**回归方法**：`hosttest bench` 同场对比 —— 「基准 A」用 `engine.LastDirectMediaUrl`（直连媒体口），
「基准 B」复用引擎已建的代理再加一层。关注：B0 应≈0ms、B3 应≪100ms、B5 第二连接应正常。
