# 混血方案：VM 只做鉴权/索引，数据面放宿主原生

> 动机：把"重活"（字节搬运）搬出 QEMU。现状是**整条链路每一层都在掉速** ——
> ARM64 引擎在 TCG 下 CPU 受限（19.5 MB/s）→ harness 代理转发（~18.9 MB/s）→ SLIRP（40 MB/s 上限）
> → 宿主。而**鉴权 / 索引那部分流量小到可以忽略**（288 B 请求 / 484 KB 响应），
> 跑在 2.9% 的 TCG 上完全无感。⇒ 按"轻重"切分，而不是按"能不能"切分。

## 1. 分层职责

| 层 | 职责 | 位置 | 流量量级 |
|---|---|---|---|
| **身份 / 鉴权** | `setImei`/`setMac`（**必须在 init 之前**）、`peerid = MAC+"004V"`、`guid`、`scid`、`Identify2.txt` | guest + 原版 `.so` | ~0 |
| **配置** | `/psdk_param`（**当前恒 404**，退回 sdk 默认配置） | guest | ~0 |
| **索引查询** | hub 查询（`hub5btmain.sandai.net`、`pool.bt.n0808.com:11400`），报文体 AES（`HubClientHttpHijackAes`） | guest + 原版 `.so` | **288 B 请求 / 484 KB 响应** |
| **资源清单** | 引擎解析出来的 URL / peer 列表 | guest →（新交接口）→ 宿主 | 待验证 |
| **数据面** | 多线程 HTTP、分片校验、落盘 | **宿主 x86 原生** | 大头 |
| **重读 / seek** | 块设备直读（已实现 4578 MB/s） | 宿主 | — |

## 2. 交接口（三选一）

| 方案 | 做法 | 评价 |
|---|---|---|
| **① `dlsym` 引擎导出方法** | `.so` 没 strip（`.dynsym` 12984 个符号），可直接调：`ResourceManager::GetDPhubResourceList` / `GetTrackerResourceList`、`TaskIndexInfo::GetQueryIndexDetail`、`ProtocolQueryCdn`+`QueryCdnResponse`(`ParseCdnInfo`) → 在 `ctrlloop.c` 加一条 `RES` 命令把清单回传宿主 | ✅ **推荐**：不复刻 AES、不逆向签名；**门槛 = 需要 aarch64 NDK 重编 harness** |
| ② 读落盘文件 | 先看 `/thunder-data` 里引擎有没有把清单/种子写出来（现有 `LS`/`CAT` 命令即可读） | 便宜，但不确定有没有 |
| ③ 现有 JNI API | `getTaskInfo` 只给速度/字节/**没有清单** | ❌ 不可用 |

## 3. 必须先验证的唯一一件事

**磁力任务的"附加资源"是 HTTP 直链，还是 peer 列表？**

- 历史日志里见过 `附加源=2`（`mAdditionalResCount`）⇒ 引擎确实拿到了附加资源
- **HTTP 直链** ⇒ 宿主多线程 HTTP 下载器即可，立刻能上 ✅
- **peer 列表** ⇒ 数据面是私有 RTMFP，宿主自研不划算 ⇒ 退回现状，或换 ARM64 宿主 ⚠️

## 4. 已知的坑（来自 `qemu-src/README.md` 的累计 11 项排除）

1. **引擎数据面在 VM 里曾长期"不起量"**（`已下载=0 速度=0`，st=1 err=0），
   排除内容/身份/配置/网络通知/目录名/种子索引/文件列表/磁盘空间/预取/子任务轮询/VodSDK 后，
   归因为「bionic 垫片未覆盖的依赖」→ **这也是"把数据面搬出 VM"的额外理由**（一石二鸟）。
2. `XLSetReleaseLog` / `XLIsLogTurnOn` **调用必崩**（SIGSEGV），别调。
3. **`setImei`/`setMac` 必须在 `init` 之前**；链尾**不能**调 `unInit`（会让 SDK 变 9102）。
4. hub 报文体是 **AES 密文**（`HubClientHttpHijackAes`，伪 Host `res.res.res.res`）——
   **不要自己解密**，这正是留给原版 `.so` 的部分。
5. `/init` 的 `MON_SECS=0` 会让自动磁力链在元数据未就绪时进第二阶段 → **SIGSEGV**（抓包必踩）。

## 5. 落地步骤（建议顺序）

1. ✅ **编译链已打通**（2026-09-21 实测）—— 工具链在 **debian 容器 `10.0.0.108`** 上：
   `/opt/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android21-clang`
   （clang 18.0.3）。⚠ README 里记的 Windows NDK 路径 `C:\Users\lvjin\...\ndk\27.0.12077973`
   在本机**不存在**，别照着走。

   ```bash
   # ① 传源码到容器
   tar -cf - src | ssh root@10.0.0.108 'mkdir -p /opt/ndk/work && tar xf - -C /opt/ndk/work'
   # ② 交叉编译（约 0.4 秒）
   ssh root@10.0.0.108 'NDK=/opt/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64/bin; \
     cd /opt/ndk/work/src && "$NDK/aarch64-linux-android21-clang" harness4.c \
       -o /opt/ndk/work/harness4 -ldl -Wl,-export-dynamic'
   # ③ 取回并打进 initrd（repack_initrd.cs 的第 2 个参数就是新 harness 路径）
   ssh root@10.0.0.108 'cat /opt/ndk/work/harness4' > harness4-new
   dotnet run repack_initrd.cs <runtimeDir> harness4-new
   ```

   实测产出：**128736 字节 ARM64 bionic PIE**，`interpreter /system/bin/linker64`。
   ⚠ `harness4.c` 是 `gen_harness4.py` 生成的**产物**：直接改 `.c` 能编译，但重跑生成器会覆盖它。
2. `ctrlloop.c` 加 `RES` 命令：`dlsym` 上面那批方法 → 以文本把资源清单回传宿主。
3. 跑一次真实磁力任务，看清单形态（§3）。
4. 形态确定后分流：
   - **HTTP 直链** ⇒ 宿主实现多线程 HTTP 下载器（分片 + Range + 校验 + 落盘），
     读侧直接复用现有 `SparseBlockStore` / 直读逻辑。
   - **peer 列表** ⇒ 停止自研，维持现状或转 ARM64 宿主。
5. **UI 侧同步**：进度/边下边播要改读宿主下载器的状态，**避免出现两套进度源**。

## 6. 预期收益与代价

| 项 | 预期 |
|---|---|
| 数据面吞吐 | 从 **19.5 MB/s**（TCG 上限）→ **≈ 宽带**（本机实测 38~41 MB/s；千兆环境可吃满） |
| 索引/鉴权延迟 | 不变（流量极小，TCG 无感） |
| 改造量 | 中：NDK 工具链 + harness 加命令 + 宿主下载器 + UI 进度对接 |
| 风险 | ① 清单可能是 peer 列表（方案失效）② 引擎数据面本来就弱，搬出来后仍需自研实现完整 BT/HTTP 语义 ③ **借原版 `.so` 的鉴权结果驱动自研下载，仍可能与其 SDK 条款冲突**（产品决策） |

**关键判断**：这条路的**下界 = 现状**（验证失败就退回，不亏），**上界 = 吃满带宽**。
所以先花最小代价把 §3 验证掉再动工 —— 不要先写下载器。
