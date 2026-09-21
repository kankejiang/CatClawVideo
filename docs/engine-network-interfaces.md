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
