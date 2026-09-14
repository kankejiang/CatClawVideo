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

## 3. okhttp 3.12.11 / okio 2.8.0

| 项 | 内容 |
|---|---|
| 文件 | `CatClawVideo.Maui/Jars/okhttp-3.12.11.jar`、`CatClawVideo.Maui/Jars/okio-2.8.0.jar` |
| 许可 | Apache-2.0 |
| 用途 | TVBox spider jar（含 Guard 加固包解密后的真实实现）直接依赖 okhttp3，宿主必须提供 |

`okio` 所依赖的 `kotlin-stdlib` 由本项目的 AndroidX / Media3 NuGet 间接引入，
**不额外打包**（重复打包会触发 D8 的 `Type kotlin.XxxKt is defined multiple times`）。

---

## 4. 运行期下载（不随包分发）

- **spider jar**：由用户配置的订阅在运行时下载（如 `fty.jar`，含 Guard 加固壳）。
  发行物内不含任何 spider jar。
- **本项目不内置任何片源**，所有源均由用户自行配置。
