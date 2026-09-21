# 迅雷引擎的网络接口清单（抓包实测）

> 目的：摸清引擎**到底在跟谁说话、用什么协议**，作为「能不能自己实现一个替代引擎（x86 原生）」的决策依据。
> 全部内容来自一次真实抓包 + guest 侧日志，命令与工具见文末。

## 0. 结论摘要

| 问题 | 答案 |
|---|---|
| 能不能抓包拿到接口？ | **能，而且不需要驱动/管理员** —— QEMU 自带 `filter-dump`，在 SLIRP 边界直接落经典 pcap |
| 接口不止 IP，还有域名吗？ | **有**。guest 内有 DNS hook（`[dns-hook]` 日志）+ pcap 里的 DNS 查询，11 个域名全部可见 |
| 拿到接口就能重写引擎吗？ | **标准 BT 部分可以；迅雷私有部分不行**（见 §4） |
| 最大的障碍是什么？ | **不是接口地址，是鉴权身份（`scid`/`scg`/`peerid`/`guid`）与私有报文体** |

## 1. 怎么抓的

QEMU 11.1.0 的 `-object help` 里有 **`filter-dump`** —— 挂在 `netdev` 上即可把通过该网卡的
报文写成标准 pcap，**不需要装 npcap/Wireshark、不需要管理员权限**：

```bash
# 在 ThunderRuntime/ 目录下（相对路径 -L share / -kernel pkg_kernel 需要它）
qemu-system-aarch64.exe -M virt -cpu max -m 5120 -smp 4 -nographic \
  -L share -kernel pkg_kernel -initrd pkg_initrd.gz \
  -append "console=ttyAMA0 rdinit=/init loglevel=4" \
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:18091-:20080 \
  -device virtio-net-pci,netdev=n0 \
  -object filter-dump,id=dump0,netdev=n0,file=C:/Code/.unidbg-probe/engine.pcap \
  -monitor tcp:127.0.0.1:18400,server,nowait > console.log 2>&1
```

⚠ **两个必须注意的点**：

1. **必须让 QEMU 优雅退出**（经 monitor 发 `quit`），否则 filter-dump 的用户态缓冲不会刷盘 ——
   直接 `taskkill /F` 会得到一个只有 24 字节文件头的空 pcap。实测：优雅退出 12051 字节 vs 强杀 24 字节。
2. 本机 shell（PortableGit 精简环境）**没有 `sleep`**，等待要用 `python -c "import time; time.sleep(N)"`。

配套解析脚本（`JavaBridge/qemu-src/tools/`）：

| 脚本 | 作用 |
|---|---|
| `pcap_analyze.py` | 汇总：TCP 外连端点、TLS SNI、明文 HTTP 请求行/Host、UDP 端点、DNS 查询名 |
| `pcap_flows.py` | 按四元组重组 TCP 流，打印明文 HTTP 的**请求与响应全文** |

## 2. 抓到了什么

### 2.1 域名（11 个，全部有 DNS 解析记录）

| 域名 | 作用推断 | 证据 |
|---|---|---|
| `conf-darwin.xycdn.com` | **配置下发**（明文 HTTP） | DNS + `GET /psdk_param` |
| `natdetection.onethingpcs.com` | **NAT 类型探测**（网心科技 = 迅雷 PCDN） | DNS（`116.132.217.99`）+ 日志 |
| `sdk1xyajs.data.p2cdn.com` | **P2CDN 数据 / 统计上报** | DNS + 日志 `dns resolve begin` |
| `master.wap.dphub.sandai.net` | 迅雷 hub（**旧，已下线**） | `/etc/hosts` → `112.64.218.71` |
| `hub5u.wap.sandai.net` / `hub5pn.wap.sandai.net` | 迅雷 hub（**在用**） | DNS（`220.202.21.137`） |
| `hub5p.sandai.net` / `pr.m.hub.sandai.net` | 迅雷 hub（`pr.m` = 服务端下发配置里的 `phub_host`） | `/etc/hosts` |
| `btrouter.sandai.net` | BT 路由（**旧，已下线成 127.0.0.2**） | `/etc/hosts` |
| `bt2.careland.com.cn` / `pool.bt.n0808.com` | BT 资源池 / tracker | DNS |
| `router.bittorrent.com` / `dht.transmissionbt.com` / `router.utorrent.com` | **公开 DHT 引导节点**（标准 BT） | DNS |
| `www.baidu.com` | **连通性探测** | DNS（`157.148.69.151`） |

★ 前 4 类（`*.sandai.net`、`*.onethingpcs.com`、`*.p2cdn.com`、`*.xycdn.com`）是**迅雷私有**；
后两类（DHT 引导、`www.baidu.com`）是**通用手段**。

### 2.2 明文 HTTP 接口（完整报文，pcap 重组）

```
GET /psdk_param?version=2.0.8.15&test=1789960582 HTTP/1.1
Accept: */*
Cache-Control: no-cache
Connection: keep-alive
Content-Type: application/octet-stream
Host: conf-darwin.xycdn.com
Pragma: no-cache
Scid: null
```

- `/psdk_param` = **P2P SDK 参数下发**（`version` = 引擎版本 `2.0.8.15`）
- `Scid: null` ⇒ **`scid` 本来是要作为身份头发出去的**（本次为 null，见 §2.4）
- 该请求在本次运行中**发了两次**（`112.86.58.13:80` 与 `119.167.205.191:80`，同域名两个 A 记录）

### 2.3 端口分布（协议指纹）

| 端点 | 协议推断 |
|---|---|
| TCP **80** | 明文 HTTP（配置下发；`Scid` 身份头） |
| UDP **8899** × 6 包（`58.240.183.248`） | **迅雷私有 P2SP 探测/心跳** |
| UDP **6881**（`38.99.5.32` / `112.64.218.71` / `103.97.176.73`） | **标准 BitTorrent DHT / peer** |
| UDP **8000**（`220.202.21.137`） | hub 侧 UDP 服务 |
| UDP **1900**（`239.255.255.250`） | SSDP（UPnP 端口映射探测） |
| **RTMFP** | 日志：`local rtmfp context peerid [4f7e3d…] port 41063` ⇒ **P2P 数据面走 Adobe RTMFP** |

### 2.4 身份字段（日志实测）

| 字段 | 值/形态 | 说明 |
|---|---|---|
| `peerid` | `4f7e3d6385442da90cce081ab279965ca8d7ba543aae125cd9b509ae4b2c7ed9` | 32 字节 hex 摘要（与 `harness4.c` 里另一处 `MAC + "004V"` 的构造**不是同一个**，需按调用点区分） |
| `scid` | `05cc4e71-20e5-477d-9cf2-3668d415ca86` → `b972b4cb-…` | 会话 ID，每次启动重新生成 |
| `scg` | **`null`** | 本次为空 —— 很可能是配置拉取失败后的降级状态 |
| `guid` | `IMEI + "_" + MAC`（见 `harness4.c`） | 设备唯一标识 |
| 上报体 | `{"act":"uc","v":"2.0.8.15","pi":"4f7e3d…","ip":"","r":-2,"c":72,"l":1,"t":1}` | `act=uc`（update config）**明文 JSON**，`c` = 错误码 |

### 2.5 本次抓包的局限（必须说明）

- 日志里出现 **`Fatal signal 11 (SIGSEGV) in tid 433 (harness)`**（链启动后约 1 秒），
  `t=40s` 只剩 3 条 TIME_WAIT、`t=160s` 已无任何连接 ⇒ **引擎活动中途夭折**，只抓到 **104 个报文 / 12 KB**。
- 原因线索：仓库 initrd 用的是**env 驱动**的自动磁力链（`MAGNET` + `MON_SECS=0`），
  而正常 App 走的是**宿主控制服务器**驱动（guest 轮询 `10.0.2.2:18080/task`）。
  本次没有任何东西在 18080 上应答 → 属于非正常路径。
- 因此 **`/psdk_param` 的响应体、hub 索引查询的请求体、P2SP 报文体都还没抓到**。

## 3. 复现步骤

```bash
PY="C:/Users/Administrator/.workbuddy/binaries/python/versions/3.13.12/python.exe"
# 1) 起 QEMU（见 §1 命令），等 170s
# 2) 经 monitor 优雅退出
"$PY" -c "import socket,time;s=socket.create_connection(('127.0.0.1',18400),timeout=5);\
time.sleep(.5);s.recv(65536);s.sendall(b'quit\n');time.sleep(2);s.close()"
# 3) 解析
"$PY" JavaBridge/qemu-src/tools/pcap_analyze.py C:/Code/.unidbg-probe/engine.pcap
"$PY" JavaBridge/qemu-src/tools/pcap_flows.py  C:/Code/.unidbg-probe/engine.pcap 900
# 4) guest 侧域名解析记录（DNS hook 直接打在控制台日志里）
grep -a '\[dns-hook\]' console.log
```

## 4. 评估：能不能「重写一个迅雷 .so」

按**可实现性**分三层：

| 能力 | 协议性质 | 可实现性 |
|---|---|---|
| **标准 BT**：DHT（`router.bittorrent.com` 等）、tracker、peer wire（6881） | **公开标准** | ✅ **现成库直接可用**（如 MonoTorrent，纯 C#、x86 原生、不受 TCG 限制） |
| **配置下发** `/psdk_param` | 明文 HTTP | ✅ 可直接调用（但要能解析其响应，本次未抓到） |
| **迅雷 hub 索引查询**（`*.sandai.net`） | 私有（HTTP/UDP 混合） | ⏳ 需继续抓完整报文才能判断难度 |
| **P2SP 私有协议**（UDP 8899）+ **RTMFP 数据面** | 私有 | ⏳ 报文格式可继续摸，但复刻工作量大 |
| **鉴权身份**：`peerid` / `guid` / `scid` / `scg` + 可能的签名参数 | 私有 | ❌ **唯一真正需要逆向 `.so` 的部分**（好在只需定位几个函数，不是整体反编译） |

**真正的卡点不是接口地址，而是**：

1. **身份与签名**：`peerid`/`guid`/`scid` 在 `.so` 内生成，服务端很可能校验其合法性；
   要复刻就得局部逆向这几个函数（这与「整体反编译成 x86」是两件不同量级的事）。
2. **服务端是否接受非原版客户端**：即使协议格式完全复刻，服务端也可能因版本/签名不合而拒绝。
3. **法律**：复刻迅雷私有协议**违反其 SDK/服务条款**，用于发布产品另有侵权与不正当竞争风险。
   ⚠ 抓包分析自己 App 的流量是合法的调试行为；**复刻并分发私有协议实现是另一回事**。

## 5. 建议

1. **要 x86 原生下载能力** → 先把**标准 BT 部分**用现成库做起来（热门资源够用），
   冷门资源回落到 QEMU 引擎。这是**唯一不碰逆向、不碰法律风险**的提速路径。
2. **想继续摸协议**（纯研究）→ 抓包工具已就位，下一步应先让**正常链路**跑起来
   （补上宿主控制服务器，或修掉那次 SIGSEGV），再抓 hub 查询与 `/psdk_param` 的响应体。
3. **产品侧性价比最高**仍是：**数据面直读**（4578 MB/s）承担重读 + HTTP 直链不经引擎。
   详见 `docs/qemu-engine-performance.md`。

## 6. 二次抓包（长跑 200s）：修正与关键补充

首次只抓到 104 个报文（harness 在链启动约 1s 时 SIGSEGV）。**根因已定位**：`/init` 里
`MON_SECS=0` ⇒ 元数据还没拿到就进第二阶段 `createBtTask(torrentPath=/thunder-data/cc-test)`
（该路径不存在）⇒ 崩溃。**改成 `MON_SECS=90` 后不再崩**，抓到 **670 KB / 1026 报文**、
元数据（484518 B）完整拉齐。（修改打在 runtime 的**副本**上，用 `tools/repack_initrd.cs` 的副本，
**没动仓库原件**。）

### 6.1 修正：迅雷服务端不是"零响应"，而是分通道

| 通道 | 结果 |
|---|---|
| **`pool.bt.n0808.com` → `112.64.218.66:11400`** | ✅ **`POST /`（288 B 加密）→ `200 OK`，`Server: openresty/1.9.15.1`，`Content-Length: 484564`** —— 引擎日志里的「已下载=484518/484518」就是它。**BT 资源池直接下发种子** |
| `hub5btmain.sandai.net` → `112.64.218.64:80` | ✅ 下行 61 KB（**新发现的在用 hub**） |
| `112.64.218.71:80`（initrd 里 hosts 钉的旧 hub） | ⚠ 下行 5.4 KB |
| `conf-darwin.xycdn.com/psdk_param` | ❌ 404（带不带 `Scid` 都一样） |
| `flowcontroll.dcdn.sandai.net:8080/query` | 请求已发出，下行 0.9 KB |

### 6.2 私有报文的形态：HTTP + AES，Host 头是伪值

```
POST / HTTP/1.1
Host: res.res.res.res:11400          ← 刻意伪造的 Host
Content-Type: application/octet-stream
Content-Length: 288
User-Agent: Mozilla/4.0
<288 字节 AES 密文>
```

对应导出符号 **`HubClientHttpHijackAes`** —— 所以抓包里看不到明文；`Content-Type` 还被写两次
（先 `form-urlencoded` 再 `octet-stream`）做伪装。

### 6.3 ★ 修正：`.so` **并没有 strip** —— 导出符号表完整

此前写「已 strip」是**错的**（我只查了 `.symtab`）。实际 **`.dynsym` 有 12984 个导出符号，
C++ 类名完整可读**。与协议直接相关的有：

| 符号 | 含义 |
|---|---|
| `ResourceManager::GetDPhubResourceList` / `GetTrackerResourceList` | **引擎内部就有「取资源列表」的方法** |
| `ProtocolQueryCdn` / `QueryCdnResponse` / `DcdnAccountsManager` / `ParseCdnInfo` | **CDN 加速查询协议** |
| `ProtocolQueryBcid` / `QueryBcidResponse` | 资源 ID（BCID）查询 |
| `ProtocolQueryXtPool` / `QueryXtPoolResponse` | 迅雷资源池查询 |
| `TaskIndexInfo::GetQueryStateInfo` / `GetQueryIndexDetail` | 索引查询状态 / 详情 |
| `HubClientHttpHijackAes` | hub 通信 = HTTP + AES |
| `rtmfp::protocol::*`（`EncodeDirectRHelloChunk` / `_CreateKey`） | P2P 数据面 = RTMFP + 非对称密钥 |
| `XLRequeryIndex` | 已导出的纯 C 救援接口 |

## 7. 「鉴权用原版 `.so`、下载用自研」这个方案行不行

**结论：技术前提已经具备，可行；而且不需要复刻 AES、也不需要逆向签名。** 依据：

1. **职责在引擎内部本来就是分开的** —— `ProtocolQueryCdn` / `ResourceManager::Get*ResourceList`
   （查资源）与下载器是不同模块，而且 `.so` **把它们导出了**。
2. **鉴权 / 索引的流量极小**：288 B 请求 / 484 KB 响应。这点流量跑在 TCG（2.9%）上**完全无感**；
   性能瓶颈（19.5 MB/s）全在数据面 —— 正好交给自研。
3. **交接口有两个候选**：
   - (a) 现有 JNI API —— 但 `getTaskInfo` 只给速度/字节，**没给 URL 列表**；
   - (b) **直接 `dlsym` 上面那几个导出方法**（因为没 strip），在 guest 内把资源清单取出来，
     经控制通道回传宿主 ⇒ **这是最干净的"混血"接缝**。
4. **仍然未知、且决定成败的一点**：资源清单里是 **HTTP 直链**（CDN/DCDN 分片地址）还是
   **peer 列表**（IP:port + RTMFP）？
   - **HTTP 直链** ⇒ 自研下载器 = 多线程 HTTP，立刻可做 ✅
   - **peer 列表** ⇒ 数据面是私有 P2P（RTMFP），自研不划算 ⚠️

   → **下一步**：让第二阶段真正跑起来（本轮 `createBtTask` 因 `torrentPath=/thunder-data/cc-test`
   不存在而返回 **9303**，DL 未开始），或直接在 guest 内调用 `GetDPhubResourceList` 打印输出。

## 9. ★★ 运行时实测：引擎的加速资源 = `HttpResource`（2026-09-21，方案 B 关键验证）

方案 B（VM 只做鉴权/索引、数据面搬宿主）成立与否，取决于**引擎手里的资源是什么形态**。
本轮用「vtable 指针扫描」在运行中的 guest 里直接逮到了 `ResourceManager` 实例并调用了它的取列表方法：

```
★ 命中 ResourceManager 实例 @ 0xffffb84dd180
  GetDPhubResourceList(inst)   → 元素数 0
  GetTrackerResourceList(inst) → 元素数 0
  GetCdnResourceList(inst)     → 元素数 0
  GetMirrorResourceList(inst)  → 元素数 3        ← 直链任务的镜像资源
    [0] obj=0xffffb84d6380  vptr=0xffffb5d71b50  == HttpResource ✅  GetResourceType() = 2
    [1] obj=0xffffb84d7880  vptr=…1b50           == HttpResource ✅  GetResourceType() = 2
    [2] obj=0xffffb84dc580  vptr=…1b50           == HttpResource ✅  GetResourceType() = 2
```

- **类的判定是硬证据**：对象首字（vptr）与 `_ZTV12HttpResource + 16` **精确相等**（运行时算出 `base+0x536b50`）。
- **`GetResourceType() = 2`** 支持**按来源分类**：`Server / Scdn / Peer` 是三个独立类别
  （`XLAddServerResource` / `XLAddScdnResource` / `XLAddPeerResource` 三个注入接口印证）。
- ⇒ **结论：引擎的加速资源是 HTTP 资源（`HttpResource`，带 `Uri`），不是 peer 列表。**
  **方案 B 的核心前提成立。**

### 可复用的技术手法（都在 `ctrlloop.c` 的 `URLINFO` 里）

| 手法 | 要点 |
|---|---|
| **vtable 扫描定位 C++ 实例** | `.so` 的 vtable 是导出数据符号：`dlsym("_ZTV15ResourceManager")` 拿到地址，**对象首字 = vtable + 16**。读 `/proc/self/maps` 拿可读区间，8 字节步进扫这个值 → 实例地址 |
| **未知签名安全调用** | 每个调用 **fork 到子进程 + 3s 超时 + SIGKILL**：崩/挂都只损失这一条（实测 `XLGetThunderzInfo` 挂死、`XLGetUrlQuickInfo` 挂死） |
| **★ ARM64 TBI tag** | 引擎的 C++ 指针高字节带 tag（`0xb400ffff…`）→ **解引用前必须 `& 0x00FFFFFFFFFFFFFF`**，否则读到别人的内存 |
| **按值返回的类** | libc++ `std::string` 是 24 字节 → 用 `struct{u64,u64,u64}` 接收（sret 由编译器处理） |
| **输出引用参数** | `GetUri(Uri&)`、`GetUserAgent(std::string&)` 这类**不能传零缓冲**（内部会做 `=`/析构）→ 必须先构造（`Uri::Uri()` 已导出） |

### ⚠ 两条死路（别再走）

1. **`XL*` 纯 C 门面（75 个接口）全部不可用** —— `XLGetUrlQuickInfo` / `XLGetTaskInfo` /
   `XLGetTaskInfoEx` / `XLGetTaskCheckInfo` **挂死**，`XLGetThunderzInfo` **SIGSEGV**（8/8 变体全失败）。
   它们是**另一套（PC SDK）门面**，在这条 JNI 路径里没有初始化。**要用 JNI 层或 C++ 对象层。**
2. **`ctrlloop` 的 `main_loop()` 排在「自动磁力链」之后** —— 自动链要跑 `MON_SECS+DL_SECS`
   （实测 90+240s），所以想用控制通道就必须用**产品配置**：`MAGNET=""`、`MON_SECS=0`、`DL_SECS=0`。

### 仍差一步

**把 URI 字符串取出来**：`Uri::Uri()` → `HttpResource::GetUri(Uri&)` → `Uri::to_string()` 的链条已跑通，
但 `to_string` 返回的 24 字节里 `size=0`（返回约定或 `this` 调整待定：`HttpResource` 多继承，
存在 `_ZThn280_` thunk）。下一步二选一：① 按「w0 是指向 `std::string` 的指针」再解一次；
② 用 `_ZThn280_N12HttpResource6GetUriER3Uri`（带 -280 调整的 thunk）传次级指针。

## 10. ★★★ 已取到完整 URL（2026-09-21，方案 B 落地依据）

接上节。**`Uri` 不是 libc++ 的 `std::string`，而是「按组件存字符串」的结构**：
对象里每个数据字几乎都是**带 tag 的指针，直接指向裸字符串数据**（首字节即文本），
组件顺序为 `w0=scheme`、`w3=host`、`w5=path`。**不要调 `Uri::to_string()`（实测会崩）**，
直接逐个组件读即可。

实测（任务 = `http://mirrors.nju.edu.cn/ubuntu-releases/24.04/ubuntu-24.04.3-desktop-amd64.iso`）：

```
[0] == HttpResource ✅  GetResourceType() = 2
      w0 = http://
      w3 = www.huaweiyes.cn
      w5 = /chfs/shared/ubuntu-24.04.3-desktop-amd64.iso
[1] == HttpResource ✅  GetResourceType() = 2
      w0 = https://
      w3 = multimedia.qfile.qq.com
      w5 = /download?appid=14901&client_type=web&client_ver=8.9.25
           &fileid=EhRLicSMfZQurLpnoUv3bBb-dCJXGhiAoPrRFyC1dCi9yuTP2ZuTAzIEcHJvZFCA6kla…&rkey=CAMSqAGmgtos…
[2] == HttpResource ✅  GetResourceType() = 2   （同 [1] 结构，另一个 fileid / rkey）
```

**结论（方案 B 的落地依据）**：

1. 引擎为同一个文件返回了 **3 个完整 HTTP 源**，其中两个是 **QQ 微云直链**、一个在 `huaweiyes.cn` 共享盘 ——
   **迅雷的"加速"本质是「资源发现」**：它替用户在其他网盘/CDN 上找到同一文件的**可下载直链**
   （注意 `rkey=` 是签名令牌）。
2. 这些 URL **就是给下载器用的**：宿主完全可以拿它们做多线程 Range 下载。
   ⚠ 待验证：`rkey` 是否绑定客户端 IP —— SLIRP NAT 下 **guest 与宿主出口 IP 相同**，所以从宿主直连**很可能可用**
   （这也是 #25 之后要实测的一步）。
3. 与 §9 的 `GetResourceType()` 三分类（Server/Scdn/Peer）一致：这里拿到的是 **Server/镜像类**资源。

### 检索顺序（复现用）

```
1. dlsym("_ZTV15ResourceManager") → vtable 地址，对象首字 = vtable + 16
2. 扫 /proc/self/maps 可读区间（8 字节步进）→ 命中 ResourceManager 实例
3. 调 GetMirrorResourceList(inst, &vec3)   → 3 个 IResource*（其它三类本次为空）
4. 每个元素掩 tag（& 0x00FFFFFFFFFFFFFF）→ 读对象首字 → 与 base+0x536b50 比对确认 HttpResource
5. 调 HttpResource::GetUri(inst, &uriBuf)（uriBuf 先用 Uri::Uri() 构造）
6. uriBuf 逐字：掩 tag → 当裸字符串读 → w0/w3/w5 = scheme/host/path
```

## 11. ❌ 关键否定结论：这些 URL **不能**交给宿主直接下载（2026-09-21）

上节拿到 3 个完整 URL 后，立刻在宿主用 `curl` 回放（**边跑边取、取到即试**）：

| 改动 | 结果 |
|---|---|
| 裸请求（浏览器 UA，IPv6 出口） | `400` `{"retcode":-5503011,"retmsg":"invalid rkey","retryflag":1}` |
| ＋`Referer` | 同上 |
| 去掉 UA | 同上 |
| **强制 IPv4**（出口变成 `175.42.242.41`，与 guest 的 SLIRP 出口一致） | 同上 |
| **改用引擎自己的 UA `Mozilla/4.0`**（抓包实测值） | 同上 |
| 引擎 UA ＋ `Referer` ＝ 原始 URL | 同上 |
| `Range: bytes=0-1023` / 无 Range | 同上 |
| `http://www.huaweiyes.cn/...`（无令牌那条） | `000`（**连不上**，非公网可达地址） |

**8 种组合全部失败**，服务端明确回 **`invalid rkey`**（腾讯 `FrontHttpd`）。

### 这意味着什么

- **`rkey` 不是"绑 IP + 绑 UA"那么简单** —— 绑的是**取令牌时的完整请求上下文/会话**
  （很可能还绑 TLS 会话或引擎 HTTP 栈的其它指纹）。
- ⇒ **「引擎去找源、宿主拿 URL 直接下载」（方案 B 的最理想形态）不成立。**
  这条路的收益预期（摆脱 TCG 的 2.9%）**无法通过复用 URL 拿到**。
- 附带发现：本次实验用的任务 URL（NJU 的 `24.04/ubuntu-24.04.3-desktop-amd64.iso`）**本身就 404**，
  但引擎仍然"找到"了同名文件的第三方副本（QQ 微云）—— 说明迅雷的**资源发现是按文件名/内容去别处找**，
  与原始 URL 是否有效无关。

### 仍然成立的三条路（按性价比）

| 路线 | 说明 | 状态 |
|---|---|---|
| **① 保持现状 + 数据面直读** | 引擎负责"找源+取字节"，重读/seek/多读者走直读（4578 MB/s，不受引擎限制） | ✅ 已实现，**目前唯一能覆盖第三方源的方案** |
| **② 宿主自研标准 BT**（MonoTorrent） | 不需要引擎，x86 原生满速；但**没有"资源发现"能力**，冷门资源变差 | 可选，成本中等 |
| **③ 直链任务不经引擎** | 产品现状（`CompositeVodSourceProvider.cs:106` 只把 `magnet:` 交给引擎），直链本来就是宿主原生满速 | ✅ 已是现状 |

**结论：方案 B 的"数据面搬宿主"无法落地；能拿到的收益仍只在"重读侧"（已由直读实现）。**
除非将来愿意做「让宿主伪装成引擎的 HTTP 栈」这种深度工作（工作量与法律风险都远超收益）。




## 8. 各方案下载速度排序（实测 + 推算）

| 方案 | 数据面谁搬 | 本机可期上界 | 源覆盖 | 改造量 |
|---|---|---|---|---|
| **A 现状**：全交 QEMU 引擎（TCG） | ARM64 引擎（2.9%） | **19.5 MB/s**（实测；CPU 受限，**不随带宽增长**） | ✅ 迅雷 P2SP 全量 | 0 |
| **B 混血**：原版 `.so` 只做鉴权/索引 + **宿主原生自研下载** | x86 原生 | **≈ 宽带上限：38~41 MB/s** | 取决于清单形态 | 中 |
| **C ARM64 宿主 + 原版引擎** | ARM64 原生 100% | 同上界（引擎不再 CPU 受限） | ✅ 全量 | 换硬件 |
| **D 纯自研标准 BT**（不用 `.so`） | x86 原生 | 热门资源同上界；**冷门资源暴跌** | ❌ 无 P2SP | 中 |

**判据**：

1. **数据面吞吐**：B、D > A（宿主原生 vs TCG 的 2.9%）；C 与 B 同档。
2. **源覆盖**：A、C ≫ D —— 迅雷 P2SP 的价值就是"冷门也有人做种"。
3. ⇒ **「最快且不丢源」= B（混血）**。

**本机的天花板是宽带，不是方案**：宿主直连互联网实测 **38.1 MB/s**，guest 经 SLIRP 纯 TCP **40.8 MB/s**
（甚至快过宿主直连 ⇒ SLIRP 不是瓶颈）。**任何方案都不可能超过它**，所以本机最多 ~2×。
换到千兆环境：B 能吃满带宽、A 仍卡在 19.5（CPU 受限）⇒ 收益放大到 **3~5×**。

**条件分支（关键，取决于 §7 的未知项）**：

- 清单是 **HTTP 直链** ⇒ B 成立，自研多线程 HTTP = 最快。
- 清单是 **peer 列表** ⇒ 自研只能用标准 BT 连标准 peer、**连不上迅雷私有 peer**，
  速度**可能反而不如 A** ⇒ 此时最快的是 **C（ARM64 宿主原生）**；没有 ARM 硬件就维持 A。


---

## 12. ★★★★ 转机：迅雷有 **Windows 原生 x86 SDK**，实测 **112 MB/s**（2026-09-21）

§11 的否定结论只否掉了「**搬 URL**」这条路。但迅雷对 **Windows 桌面**本来就发布过原生引擎，
**既不需要模拟、也不需要搬 URL** —— 直接调它自己的 API。

### 12.1 来源（三层证据）

1. **官方文档**：`http://open.xunlei.com/wiki/api_doc.html`（迅雷开放平台「迅雷下载引擎」API 文档，
   逐条对应 `XL_Init` / `XL_CreateTask` …）。
2. **OEM 集成**：小米（MIUI 系统库 + `com.android.providers.downloads.XlTaskHelper`）、猎豹、
   360 极速浏览器、迅雷 7 本体。独立佐证：`libxl_thunder_sdk.so` 的导出符号含 Jenkins 路径
   **`dl_miui_union_master-…/downloadlib/src/main/cpp/dl_miui_downloadlib/`** ⇒ 迅雷×小米联合构建。
3. **社区归档**：`cryzlasm/ThunderOpenSDK`（README 原文：*"版权与最终解释权归迅雷公司所有 /
   迅雷下载引擎 / 分别由 小米, 猎豹, 360极速浏览器等软件提取"*），收录 5 套 OEM 提取版 +
   官方头文件 `xldl.h`；另有 `megahertz0/android_thunder`、`ZoranLi/thunder`（自述"迅雷apk反编译"）
   提供 Java 层全量反编译源码与 40 个 `parameter` 参数类。

### 12.2 运行时组成与架构（实测 PE 头）

| 文件 | 架构 | 作用 |
|---|---|---|
| `xldl.dll`（293 KB） | **i386 / x86 32 位** | 外壳 API，**31 个 `XL_*` 导出**，与 `xldl.h` 逐字对上 |
| `download_engine.dll`（3.35 MB） | **i386 / x86 32 位** | 真引擎，**139 个明文 C 导出**（Android 那份是 12984 个 C++ mangled） |
| `MiniThunderPlatform.exe` | x86 | 引擎宿主进程（头文件有 `TASK_ERROR_TP_CRASHED=0x42 "MINITP崩溃"`） |
| `dl_peer_id.dll` / `dc.ini` / `id.dat` / `version.data` | — | 设备标识与版本（`id.dat` = `[partner] id = 80000043`；`version.data = 1.2.141023`） |

★ `download_engine.dll` 的明文导出直接解释了 §9 里用 vtable 扫描才挖出的东西：
**资源搜索引擎四路** `dhub_searcher` / `p2p_res_searcher` / **`p2s_res_searcher`** / `phub_res_searcher`；
**CID 索引** `bcid_calculator` / `cid_store` / `get_task_gcid`；
**协议命令** `cmd_query_res_info_hub` / `cmd_query_p2phub` / `cmd_query_hub`；
**来源统计** `bytes_from_dphub_res` / `bytes_from_nondphub_res`（↔ 反推得到的 `mScdnSpeed`/`mPcdnSpeed`）；
**边下边播** `create_predownload_task` / `start_predownload_task`；**块级读取** `get_downloaded_blocks`。

### 12.3 实测结果

测试程序：`C://Code//.tvbox-ref//xldl-probe//test//`（.NET 10，`PlatformTarget=x86` + `RuntimeIdentifier=win-x86`），
部署到 `..\sdk\`（与 `xldl.dll` 同目录）运行。结构体布局自校验：
`SizeOf(DownTaskParam)=26564` ✅ `SizeOf(DownTaskInfo)=1429` ✅（与 `#pragma pack(1)` 逐字节吻合）。

| 项 | 结果 |
|---|---|
| `XL_Init()` | **= 1**（125 ms），`MiniThunderPlatform.exe` 正常拉起/退出 |
| HTTP 直链任务 | ★★★ **112~113 MB/s**（NJU Ubuntu ISO 6347 MB，23 s 下 1.92 GB） |
| `https://` 任务 | ✗ 4 s 后 ERROR —— **2014 引擎不支持现代 TLS**（走 WININET） |
| 磁力 / 种子文件路径 | ✗ `ID_INVALID (0x43)` |
| `XL_CreateBTTaskByThunder()` | ✗ `0x80040154 REGDB_E_CLASSNOTREG` ⇒ 是去 COM 拉起迅雷7本体的壳，不是引擎入口 |

**速度对照（同机同源）**：

| 路径 | 速度 | 相对 |
|---|---|---|
| ★ **原生 x86 迅雷引擎** | **112 MB/s** | **5.7×** |
| QEMU 内 wget 纯 TCP | 40.8 MB/s | 2.1× |
| 宿主 curl 直连 | 38.1 MB/s | 2.0× |
| QEMU TCG 模拟 ARM64 引擎 | 19.5 MB/s | 1× |

### 12.4 结论修正

- §11「能不能绕开 TCG」的答案要改：**不能搬 URL，但可以换平台**。
- **直链 / 网盘 / HTTP 类任务**：Windows 上**可零模拟跑原生满速**（112 MB/s）。
- **磁力类**：本版 2014 SDK 的 BT 索引路径已废（`ID_INVALID`）⇒ 磁力暂时仍走 QEMU 的
  Android 引擎（2015 世代，BT 可用）。想让磁力也原生，需找**更新一代**的 Windows 迅雷 SDK。

### 12.5 产品化要点（踩过的坑）

1. **必须 32 位进程**：x64 宿主加载 x86 DLL 直接 `BadImageFormatException`
   ⇒ 走 **x86 助手进程 + IPC**（与现有"引擎独立进程"同构，但零模拟）。
2. **`DownTaskParam.nReserved1 = 5`** 是官方构造函数写死的魔法值，零初始化语言（C#/Go）必须手动补。
3. **`https://` 会直接失败** ⇒ 直链任务需回落 http，或由宿主先代理一次。
4. 引擎**预分配**完整尺寸临时文件：`<name>.dl` + `<name>.dl.cfg`（任务位图）。
5. ⚠ 该 SDK 仍是**从第三方软件提取的闭源二进制**，授权性质与 §1 打包的 `libxl_thunder_sdk.so` 同级
   （partner id 硬编码、可被迅雷随时失效）。


---

## 13. ★★★★★ 磁力的正解：现代迅雷引擎 `xunlei-pan-cli` 3.23.5（x86_64 原生 + 完整 BT + 边下边播）

### 13.1 先排除：§12 那个 Windows OEM 引擎**没有 BT**

解析 `download_engine.dll`（2014 版）的导出表与全量字符串：
**`bt` / `torrent` / `magnet` / `btih` / `announce` / `info_hash` 命中数全为 0**。
⇒ 它是**浏览器 OEM 版（纯 P2SP 镜像加速）**，
磁力任务报 `ID_INVALID (0x43)` 的真因是 **BT 模块压根没编译进去**，不是调用方式错。

### 13.2 现代引擎：迅雷官方 NAS 套件（可直下）

| 项 | 值 |
|---|---|
| 下载 | `https://down.sandai.net/nas/nasxunlei-DSM7-x86_64.spk`（实测 206 + `application/octet-stream`，**25.8 MB**） |
| SPK INFO | `package="pan-xunlei-com"` · `version="3.23.5-0814080017"` · `arch="x86_64"` · `maintainer="深圳市迅雷网络技术有限公司"` · `adminport=21603` |
| payload | ★ **`bin/bin/xunlei-pan-cli.3.23.5.amd64`（59.86 MB, x86_64 ELF）= 引擎本体** + `xunlei-pan-cli-launcher.amd64`(18.8 MB) + `ui/index.cgi`(16.7 MB) |
| 运行语言 | **Go 外壳**（`gitlab.xunlei.cn/xlppc/pan-cli/pkg/service.proxyToLocalUrl`、`download_runner`）+ **C++ 下载库**（`xldownloadlib` / `DownloadLib` / `P2spTask` / `HLSTask`） |

### 13.3 ★ 引擎自带能力（字符串实证）

**BT / 磁力 —— 完整协议栈**
`magnet:?` · `xt=urn:btih:` · `announce` / `announce-list` · `info_hash` ·
**`libtorrent`** · `DHT` · `ut_metadata` / `ut_pex`（BEP9/11）· `bep_00`；
DHT 实现细节齐全：`Announce peer!` / `Announce_peer with no info_hash` / `...wrong token` /
`...forbidden port %d`

**★ 边下边播（`VodPlayServer`）—— 与 Android 版 `getLoclUrl` 同源同构**
`VodPlayServer::Init / GetLocalUrl / OnTcpAccept / OnSessionPlay / SynPlayPos / SynPlaySpeed /
SynPlayState / SynPlayCached / SynPlayBitrate / PathSign` ·
`TaskManager::GetLocalUrl[ById]` · `DownloadLib::GetLocalUrl[ById]` ·
`xldownloadlib::GetLocalUrlCommand` · `XLGetLocalUrl` / `XLGetLocalUrlById` ·
日志格式 **`TaskId=%llu, ret=%s, play_url=%s`**

**★ 另有 HLS 转码**：`/transcodeplay/task/concise%v.m3u8?resolution=%v` · `HLSTask::ParseVodPlayUrl`

**服务端集群（新一代 `.v6.` 命名）**
```
hub5btmain.v6.shub.sandai.net  hub5idx.v6.shub.sandai.net  hub5u.v6.phub.sandai.net
hub5pr.v6.phub.sandai.net      pool.v6.bt.n0808.com        bt.box.n0808.com
btinfo.sandai.net   hubciddata.sandai.net   dcdnhub.dcdn.sandai.net   dcdnhub.xfs.xcloud.sandai.net
api-pan.xunlei.com  pan.xunlei.com  nas.xunlei.com  speedup.xunlei.com
```

### 13.4 客户端对接（社区已验证的形状）

- 引擎本地接口是 **`/index.cgi`**（CGI）+ `/drive/v1/*`；任务类型 `user#download-url` / `user#download`
- 社区载体 **`cnk3x/xunlei`（2041★，2026-09 仍在更新）**：把它容器化跑起来，
  `embed/authenticate_cgi` 负责**设备伪装**以通过迅雷的绑定校验，面板扫码登录
- 社区 REST 封装 **`myth815/xunlei-api`**：`/v1/resources/resolve`（投递链接）·
  `/v1/resources/torrent`（上传种子解析）· `/v1/tasks` 系列 · 进度/暂停/恢复/重试

### 13.5 ⇒ 结论

**磁力可以做到零模拟（原生满速），但载体从「Windows DLL」换成了「Linux x86_64 服务」：**

| 落点 | 说明 |
|---|---|
| **`10.0.0.108`**（x86_64 Debian LXC，20 核 / 227 GB 空闲） | 现成可用，局域网内延迟极低 |
| 本机 **WSL2** | 需 `wsl --install`；同架构 ⇒ 零模拟，且 localhost 与 Windows 互通 |
| PVE 宿主（10.0.0.100） | 也可，但会与虚拟机争资源 |

CatClawVideo 侧只需新增一个"远程引擎"后端：投递磁力 → 轮询任务 → 取 `play_url` 播放。
**唯一外部依赖是迅雷账号登录**（`cnk3x/xunlei` 已解决设备伪装 + 扫码登录这一步）。

⚠ 与 §1/§12 同样的授权性质说明：该引擎为迅雷官方发布、但**以 NAS 套件形式分发**，
对接其未公开接口属逆向使用，需自行评估合规性。


### 13.6 ★ 更正：登录是「面板 / 云盘」的门槛，**不是 BT 引擎的门槛**

13.5 写的"唯一外部依赖是迅雷账号登录"**是错的**。查 `xunlei-pan-cli.3.23.5.amd64` 的符号表后更正：

**① 引擎导出 19064 个动态符号，其中 1010 个是同一族下载库**
`xldownloadlib::{CreateP2SPTask, SetUserId, SetAccelerateToken, RemoveAccelerateToken,
GetPremiumResInfo, GetTaskInfoEx, SetMiUiVersion, SynPlayState, GetLocalUrl, GetHttpHeaderInfo}Command` ·
`DownloadLib::{SetPipeLimit, SetEmuleSwitch, GetPremiumResInfo, GetFileNameFromUrl}` ·
以及 `VodPlayServer::*`（边下边播）、`TaskManager::GetLocalUrl*`

★★ **`SetUserIdCommand` 与 `SetAccelerateTokenCommand` 是彼此独立、各自可选的命令**
⇒ 不设 userid、不设加速令牌**照样能建任务**（走 DHT / tracker / P2P）；
**只有「会员超级加速」（P2SP 镜像 + 迅雷自有节点）才需要加速令牌**。

**② 匿名通道的门牌与 TVBox 同款**
`app_key=%s, app_name=%s, app_version=%s, peer_id=%s, guid=%s` · `GlobalInfo::GetAppKey()` ·
`IMEI=%s` · `gen deviceid by nas` · `GetDeviceID GetFileKV().Get device_id`
⇒ **appKey + 伪造 device_id / peer_id / guid = 匿名**，
与 TVBox 的 Android SDK（以及本项目 Android 版伪造 IMEI/MAC 的做法）**完全是同一套机制**。
（`SetMiUiVersionCommand` 进一步说明这支与 Android 那支同源。）

**③ 所有"登录"字样都指向 UI，不指向引擎**
`"user not login"` / `"no login"` 只是**错误码枚举里的一项**（与 `no_anode` / `no_error` / `no_proxy` 并列）；
`unlogin-cinema-*.png`、`.unlogin-*` 是 **Web UI 的 CSS 类名**；
`withOtherAuthAndQrcodeLogin` 是 **Vue 前端扫码登录组件**；
`401 unauthorized` / `WWW-Authenticate` 来自静态链接进来的 Go 标准库 / openssl / nmap 指纹库。

**④ 为什么 TVBox 不用登录而 NAS 套件要 —— 产品形态不同，不是技术限制**

| | TVBox / 本项目 Android | 迅雷 NAS 套件 |
|---|---|---|
| 本体 | 从 OEM 产品里**提取的裸 Android SDK** | 迅雷**官方产品** `pan-xunlei-com` |
| 官方是否管得到 | 管不到（无产品形态） | 管得到（有自己的面板与账号体系） |
| 认证 | 硬编码 appKey + 伪造设备标识（匿名） | **同样有 appKey 匿名通道**，但**面板 UI 默认走扫码登录** |
| 登录换来了什么 | — | 云盘、远程下载推送、**会员超级加速** |

### ⇒ 行动修正

**不要走面板登录。** 正确做法是**像 TVBox 那样绕过面板直接驱动引擎**：

1. 在 x86_64 Linux 起引擎（`10.0.0.108` 或 WSL2）
2. **不经 `index.cgi` 面板**，直接驱动 —— 两条路：
   - (a) 直接 POST `/index.cgi`（可能仍需过设备校验）
   - (b) ★ **像本项目在 Windows 上互操作 `xldl.dll` 那样，写个 Linux 小程序链接它导出的
     `xldownloadlib::*Command` / `DownloadLib::*`** —— **1010 个明文 C++ 符号，比 Windows 那版还全**
3. 喂 **appKey + 伪造 device_id / peer_id / guid**（可对齐 2014 版 `id.dat` 的 `[partner] id = 80000043`）

⚠ 待实验确认的只剩一点：**迅雷是否对未登录态限制 BT 索引访问**
（`hub5btmain.v6.shub.sandai.net` / `pool.v6.bt.n0808.com`）。若受限，再评估是否值得登录。


### 13.7 ★ 已把它跑起来了 —— 但它的任务 API 全部强制账号鉴权（2026-09-21 实测）

13.5/13.6 说「差平台校验、不是登录」。**平台校验已经由本项目破解并跑通，剩下卡住的确实是账号。**

#### (1) 平台门破解（可复现，在 x86_64 Debian LXC 上实测）

```bash
# 包：nasxunlei-DSM7-x86_64.spk → tar -xf → package.tgz（★ 实为 xz，用 tar -xJf）
# ★★ 关键两步：伪造群晖平台身份
printf 'unique="synology_bromolow_3615xs"\n' > /etc/synoinfo.conf
#   unique 必须是 synology_<board>_<model> 形式；写 32 位 hex 会报 `<值> format error`
mkdir -p /usr/syno/synoman/webman/modules
cat > /usr/syno/synoman/webman/modules/authenticate.cgi <<'EOF'
#!/bin/sh
echo "Content-Type: application/json"; echo ""; echo '{"success":true,"is_admin":true}'
EOF
chmod 755 /usr/syno/synoman/webman/modules/authenticate.cgi

# 启动（照抄 SPK 的 scripts/service-setup）
PLATFORM="群晖" OS_VERSION="synology_bromolow_3615xs dsm 7.0-40759" \
ConfigPath=... DownloadPATH=... HOME=... \
bin/xunlei-pan-cli-launcher.amd64 -launcher_listen=127.0.0.1:5051 -pid <文件> -logfile <文件>
```

定位手法（值得复用）：`strace -f -e trace=openat,newfstatat,access` 过滤 `ENOENT`
→ 一眼看到它要 `/etc/synoinfo.conf`、`…/authenticate.cgi`、`pan-cli.debug.secret.mod`；
再对字段值做**二分**（改值后报错从 `key file lost:<A>` 变 `<B>` = 值被接受，变 `format error` = 格式非法）。

**结果：引擎完整启动**

```
> detect platform: synology X9ibISwpIp8jQ4Ya          ← 平台门通过
InitGlobalConfig url: https://conf-m-ssl.xunlei.com/… → resp_code=200 len=25137
LISTEN 127.0.0.1:5050 / *:21603
GET http://127.0.0.1:5050/  →  200（Web 面板）
```

#### (2) 但任务 API 全部 403

```
POST /drive/v1/resource/list  → 403 {"error":"permission_deny: checkAuth failed:token contains an invalid number of segments"}
POST /drive/v1/task           → 403（同上）
GET  /drive/v1/privilege/…    → 403（同上）
引擎日志：VerifyToken err: token contains an invalid number of segments
```
**⇒ 是 JWT 鉴权，且"解析磁力"（`resource/list`）与"建任务"（`task`）同样受保护。**

#### (3) 为什么这是产品设计而非可绕的 bug

该引擎的任务类型是 **`user#download` / `user#download-url`** —— 迅雷把 NAS 版的下载做成
**"云端任务挂在你的账号下，本地引擎只是执行器"**。日志里持续 `WaitForLogin scene=NextTask`
（任务系统在等登录），`uploadWatcher` / `watchUserChange` 也在等凭据。

#### (4) ⇒ 方案的最终裁决

| 方案 | 需账号 | x86 原生 | BT | 备注 |
|---|---|---|---|---|
| **A. 现代 NAS 引擎 3.23.5** | ❌ **需** | ✅ | ✅ | 云盘加速；本方案已跑通，只差登录 |
| **B. 现状：QEMU + Android ARM 引擎** | ✅ 不需 | ❌ 模拟 | ✅ | ~50 Mbps，已验证可用 |
| C. 纯 BT（MonoTorrent 等） | ✅ 不需 | ✅ | ⚠️ **无速度** | 实测 0.1~0.3 Mbps，已排除 |
| D. Windows 2014 OEM SDK | ✅ 不需 | ✅ | ❌ **无 BT** | 对磁力无用 |

**结论：「x86 原生 + 有 BT」与「免登录」在当前迅雷产品线上不可兼得。**
免登录且能跑磁力的只有 Android ARM 那支（= 现状 QEMU 路线）。
若要吃 A 的收益（去 QEMU + 引擎新一代 + 云盘加速），**必须接受迅雷账号登录**。
建议：**A/B 并存**（`ChainedMagnetEngine` 已是链式）—— 配了账号走 A，未配走 B。


---

## 14. ❌ 否定结论：**OEM 解包拿不到「免登录 + x86 原生 + BT」**（2026-09-21 穷举）

> 起因：既然 Android 版 SDK 是从 OEM 软件里提取的（并且它免登录），那 360 浏览器也有
> Windows/Linux 版，是否能从那里拿到**免登录**的 x86 引擎？—— **思路对，产物否。** 逐项验证如下。

### 14.1 360 浏览器 Linux 版：不含迅雷模块

官网 `https://browser.360.cn/se/linux/` 可达，页面内给出真实直链：

```
down.360safe.com/gc/browser360-cn-stable_10.6.1000.37-1_amd64.deb
down.360safe.com/gc/browser360-cn-beta_10.95.1003.29-1_amd64.deb
（同目录另有 arm64 / loongarch64 / mips64el 的 deb 与 rpm）
```

下载解包实测（各 **96.6 MB**）：**仅 103 个文件，纯 Chromium 文件集**；
`.so` 只有 `libGLESv2 / libvk_swiftshader / libEGL / libvulkan`；
**迅雷相关文件 0 个**（唯一 `xunlei` 字样出现在 `skin/iframe.srx`）。
⇒ Linux 版是**信创精简版**（麒麟/UOS 适配），商业加速模块被裁掉。

### 14.2 五个 OEM 版「真引擎」全部无 BT

对 `cryzlasm/ThunderOpenSDK` 各套的 `download/download_engine.dll` 逐个做字符串检测：

| 引擎 | 体积 | sha256 前 12 | magnet | btih | info_hash | libtorrent |
|---|---|---|---|---|---|---|
| `360Jisu_Thunder_Cloud` | 3.35 MB | `ce09928019dd` | 0 | 0 | 0 | 0 |
| `xiaomi_Thunder_Cloud` | 3.35 MB | `ce09928019dd` ★ 与 360 版**同一份** | 0 | 0 | 0 | 0 |
| `liebao_Thunder_Cloud` | 3.24 MB | `5d9ca72cfafa` | 0 | 0 | 0 | 0 |
| `0.CurUseCommonLib` | 3.35 MB | — | 0 | 0 | 0 | 0 |
| `ashe27/XLDownload` | 3.35 MB | `440d13b2bd09` | 0 | 0 | 0 | 0 |

★ `xldl.dll`（外壳，293 KB）里**确实有** `magnet:?xt` / `urn:btih` / `XL_CreateBTTask*`
（`xiaomi` 版最全，30 个 `XL_*`），但那些只是 **URL 识别字符串 + 转发壳**：
`XL_CreateBTTaskByThunder()` 走的是 COM（实测返回 `0x80040154 REGDB_E_CLASSNOTREG`），
即**把 BT 任务转交给"已安装的迅雷客户端"**，引擎自身不实现 BT。

### 14.3 ★ 产品规律（别再逐家解包试）

| 类型 | BT | 登录 | 平台 |
|---|---|---|---|
| 浏览器 OEM 加速模块（360 / 猎豹 / 小米 `xldl`） | ❌ 无 | 免登录 | Win x86 |
| 迅雷官方旧版下载库（`XLDownload.dll`） | ❌ 无 | 免登录 | Win x86 |
| **Android SDK（`libxl_thunder_sdk.so`）** | ✅ **有** | **免登录** | **仅 ARM** |
| NAS 套件（`xunlei-pan-cli`） | ✅ 有 | **要登录** | Linux x86_64 / arm64 |
| 桌面客户端（迅雷 11） | ✅ 有 | 要登录 | Win |

**规律：迅雷只在「完整产品」里交付 BT；而完整产品要么要求登录（NAS / 桌面客户端），
要么只有 ARM（Android）。OEM 那批全是「浏览器下载加速模块」→ 天生无 BT。**

### 14.4 ⇒ 结论

**「免登录 + x86 原生 + 有 BT」在当前迅雷产品线上不存在。**（§13.7 + 本节 + 纯 BT 实测，三重穷举）

目前可选的只有：

| 方案 | 免登录 | x86 原生 | BT | 边下边播 |
|---|---|---|---|---|
| **A. QEMU + Android ARM 迅雷引擎**（现状） | ✅ | ❌ 模拟 | ✅ | ✅ |
| B. 现代 NAS 引擎（已跑通，见 §13.7） | ❌ 要登录 | ✅ | ✅ | ✅ |
| C. 纯 BT 引擎 | ✅ | ✅ | ⚠️ 无速度 | — |
| ★ **D. 用 Android 设备当引擎** | ✅ | ✅（真实 ARM） | ✅ | ✅ |

**D 是目前唯一没做过的「免登录 + 原生 + 有 BT」方案**：Android 版本身就是免登录 + 原生 ARM +
满速 + 有 BT + 边下边播；PC 版把手机当下载引擎（局域网投磁力 + 拉流）。
基础设施已有：`LinkServer`（配对）、Android 侧 `ThunderP2P` / `JpP2P` 本地 httpd。
落地只需：Android 侧把回环服务暴露到局域网 + PC 侧加 `RemoteThunderEngine`
（实现 `IPreferredMagnetEngine`，挂进 `ChainedMagnetEngine` 链）。

## 13.8 ❌ 终局否决：NAS 引擎的登录需要**邀请码**（2026-09-21 实测，别再试）

§13.5/§13.6 把希望押在「像 TVBox 那样绕过面板、匿名驱动引擎」。§13.7 实测任务 API 强制 JWT 后，
本轮把引擎**完整跑起来并走到登录环节**，结论：**这条路走不通，与实现无关**。

### 实测链路（全部可复现）

1. 引擎在 x86_64 Debian（`10.0.0.108`）上启动成功（平台伪装沿用 §13.7 的 synology 两步）：
   ```
   detect platform: synology X9ibISwpIp8jQ4Ya
   Client.DoLoginQrcode → https://xluser-ssl.xunlei.com/v1/auth/device/code  resp_code=200
   Client.startWatch&deviceCode=AWqx…
   ```
2. 它给出的登录地址是扫码/设备码流程：
   ```
   https://pan.xunlei.com/yc/?client_id=X9ibISwpIp8jQ4Ya&platform=synology&privilege=PAN_CLI_PREVIEW&space=device_id%23…&user_code=AWqx…
   ```
3. **用户用自己已登录的迅雷账号扫码 → 页面要求「邀请码」才能继续** ⇒ 无法完成登录。

### 为什么这不是"再想想办法"能绕过的

- 该引擎是迅雷 **NAS 套件（内测/预览形态）**，登录换取的是 `PAN_CLI_PREVIEW` **预览权限**；
  平台标签里也明写着 `withPreviewPrivilege`/`withQrcodeLogin` —— **权限由迅雷服务端按账号发放**，
  本地伪造平台身份能过"平台门"，但过不了"账号门"。
- 任务模型是 `user#download`（云端任务挂在账号下，本地引擎只做执行）⇒ **没有有效账号 = 没有任务系统**，
  连"解析磁力"（`resource/list`）都被同一把 JWT 挡住（§13.7 实测 403）。
- 因此 §13.6(b)「符号直驱绕开 HTTP/JWT」**也不值得再投入**：即便把 `DownloadLib::CreateBtTask`
  调通，引擎内部的 `TaskManager` 仍在 `WaitForLogin scene=NextTask` 等账号凭据。

### 补充踩坑（复现时省时间）

- 引擎**不会自动续签二维码**：`DoLoginQrcode` 只在启动时执行 1 次，过期只能重启进程。
- 启动必须给 **pty + `TERM`**，否则 panic：`open /dev/tty` / `termbox: TERM environment variable not set`。
  用 `tmux new-session -d` 最稳（`script` 配 `</dev/null` 会读 EOF 退出，把引擎一起带走）。
- `ConfigPath` 传**父目录**（引擎自己拼 `.drive`）；`pkill -f xunlei-pan-cli` 会连自己那条 ssh 一起杀。

### ⇒ 桌面端提速的最终可行集（收敛）

| 路线 | 免账号 | 提速 | 状态 |
|---|---|---|---|
| **① 提高数据面直读覆盖率** | ✅ | 重读/seek/重播 4578 MB/s（首次拉取仍受引擎限制） | **推荐，纯软件** |
| ② ARM64 宿主（Windows on ARM / ARM64 Linux） | ✅ | 原生 100%（30 MB/s 量级） | 需换硬件 |
| ③ NAS 引擎（本机 x86 原生） | ❌ 需邀请码 | 原生 | **本轮否决** |
| ④ 混血：VM 找源 + 宿主下载 | ✅ | — | §11 已否决（rkey 绑会话） |
