# 第三方组件声明（Third-Party Notices）

本文件登记本项目**随发行物分发**的非自研组件。除下列内容外，仓库内代码
（`CatClawVideo.Core` / `CatClawVideo.Maui`）均为本项目原创，以 MIT 许可发布。

> 注意：**引入下列组件不改变仓库内自研代码的 MIT 许可**，但它会影响**发行物（APK）整体**
> 的许可义务。若你二次分发本项目的 APK，请自行确认合规性。

---

## 1. libp2p.so（荐片 P2P 引擎）⚠️ 许可需注意

| 项 | 内容 |
|---|---|
| 文件 | `CatClawVideo.Maui/Platforms/Android/jniLibs/arm64-v8a/libp2p.so`（5.29 MB）<br>`CatClawVideo.Maui/Platforms/Android/jniLibs/armeabi-v7a/libp2p.so`（3.80 MB） |
| 来源 | TVBox 参考仓库（`sourceCode-ccc25f6`）`player/src/main/jniLibs/` |
| 上游许可 | 该仓库整体为 **AGPL-3.0** |
| 本体权属 | 荐片厂商的**闭源二进制**；上游仅以二进制形式分发，**无源码、无头文件、无明确独立许可声明** |
| 用途 | 提供本地 P2P HTTP 服务，使「荐片」（`csp_Jianpian`）源可播放 |
| ABI 限制 | **仅 arm64-v8a / armeabi-v7a**。上游不存在 x86_64 版本，因此 x86_64 设备无法使用荐片 |

**为什么必须打包**：荐片爬虫自身不做下载。它在 `playerContent` 内部逐个探测
`127.0.0.1:9978 … 9999` 寻找宿主提供的本地 P2P HTTP 服务；探不到就会拼出端口位为空的地址
（`http://127.0.0.1:/文件名`），播放器随即报 `Source error`
（底层 `MalformedURLException: invalid port: -1`）。这是宿主能力缺失，不是爬虫 bug。

**风险提示**：将本文件打入公开发布的 APK，等于分发一份 AGPL 派生 + 权属不明的二进制。
本项目已在知情前提下选择打包；如你对此有疑虑，删除该目录、`com/p2p/P2PClass.java`
及 csproj 中对应的 `AndroidNativeLibrary` 项即可移除该能力（荐片将无法播放，
其它源不受影响）。

---

## 2. com.p2p.P2PClass

| 项 | 内容 |
|---|---|
| 文件 | `CatClawVideo.Maui/Platforms/Android/com/p2p/P2PClass.java` |
| 来源 | TVBox 参考仓库 `app/src/main/java/com/p2p/P2PClass.java`（AGPL-3.0） |
| 说明 | 上表 `libp2p.so` 的 JNI 包装类 |

⚠️ **包名 / 类名 / 方法签名不可修改**：`.so` 使用**静态 JNI 符号**绑定
（`Java_com_p2p_P2PClass_doxstarthttpd` 等），且二进制内无 `JNI_OnLoad`（非动态注册）。
改名会直接 `UnsatisfiedLinkError`。

---

## 3. 迅雷下载引擎 SDK（磁力优先）⚠️ 许可与本文件第 1 节同一量级，但授权性质更明确

| 项 | 内容 |
|---|---|
| 文件 | `CatClawVideo.Maui/Jars/thunder.jar`（79 KB，`com.xunlei.downloadlib.*`）<br>`CatClawVideo.Maui/Platforms/Android/jniLibs/arm64-v8a/libxl_thunder_sdk.so`（5.35 MB）<br>`.../arm64-v8a/libxl_stat.so`（0.80 MB）<br>（`armeabi-v7a` 同名两份） |
| 来源 | TVBox 参考仓库 `app/libs/thunder.jar` + `player/src/main/jniLibs/` |
| 上游许可 | 该仓库整体为 **AGPL-3.0** |
| 本体权属 | **迅雷（Xunlei）商业闭源 SDK**；仅以二进制形式分发，无源码 |
| 用途 | 磁力播放时**优先**走迅雷 P2SP 私有网络（中心化种子索引 + 自有节点），失败回落内置 BT |

**为什么需要**：新6V 这类站的磁力，公共 BT swarm 极薄甚至已死（同一条磁力实测仅 0.28 Mbps），
而迅雷 P2SP 能秒出种子文件列表（TVBox 的「一个磁力展开成 11 集」即来源于此）并流畅播放。

**⚠️ appKey 属未授权凭据**：`XLTaskHelper.init(context, appKey, version)` 的 appKey 是
从 TVBox 源码里取出的**硬编码字符串**（所有 TVBox 分支共用同一把），
等于用他人凭据访问迅雷服务。这与单纯「再分发闭源二进制」不同，风险更高。
此外 SDK 内的 `XLAppKeyChecker` 含 `verifyAppKeyExpired` / `"appkey expired."` ——
**该凭据有有效期，且迅雷可随时使其失效**，届时本功能会自动回落内置 BT。

**隐私**：SDK 内的 `XLUtil` 有采集 IMEI / MAC / SSID / PeerId 等设备标识的字段。
本项目**不读取真实设备标识**：伪造随机 IMEI/MAC（对齐 TVBox 的做法）并持久化，
使同一设备上保持稳定。用户可在设置中关闭「磁力优先使用迅雷」。

**只打包两个库**：`XLLoader` 仅 `loadLibrary` `xl_stat` 与 `xl_thunder_sdk`
（`libplayer.so` / `libijkffmpeg.so` 是 ijkplayer，与本 SDK 无关，未打包）。
依赖的 `liblog/libz/libstdc++/libm/libc/libdl` 均在 Android 公开库白名单内。
**仅 arm64-v8a / armeabi-v7a**，无 x86_64。

**移除方式**：删除 `Jars/thunder.jar`、`jniLibs/*/libxl_thunder_sdk.so`、`jniLibs/*/libxl_stat.so`、
`Platforms/Android/com/catclaw/video/ThunderBridge.java`、`Platforms/Android/ThunderP2P.cs`，
以及 csproj 中对应的 `AndroidJavaLibrary` / `AndroidNativeLibrary` 项即可 ——
磁力会自动全部走内置 BT，其余功能不受影响。

---

## 4. okhttp 3.12.11 / okio 2.8.0

| 项 | 内容 |
|---|---|
| 文件 | `CatClawVideo.Maui/Jars/okhttp-3.12.11.jar`、`CatClawVideo.Maui/Jars/okio-2.8.0.jar` |
| 许可 | Apache-2.0 |
| 用途 | TVBox spider jar（含 Guard 加固包解密后的真实实现）直接依赖 okhttp3，宿主必须提供 |

`okio` 所依赖的 `kotlin-stdlib` 由本项目的 AndroidX / Media3 NuGet 间接引入，
**不额外打包**（重复打包会触发 D8 的 `Type kotlin.XxxKt is defined multiple times`）。

---

## 5. 运行期下载（不随包分发）

- **spider jar**：由用户配置的订阅在运行时下载（如 `fty.jar`，含 Guard 加固壳）。
  发行物内不含任何 spider jar。
- **本项目不内置任何片源**，所有源均由用户自行配置。
