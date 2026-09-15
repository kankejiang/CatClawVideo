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
