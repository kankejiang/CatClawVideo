<img src="CatClawVideo.Maui/Resources/Images/ic_brand.png" width="180" alt="CatClawVideo" align="right" />

<div align="center">

# 猫爪影视 CatClawVideo

_Modern cross-platform video player built with .NET MAUI._

> 一只会找片的猫爪喵 —— 本地、私域、订阅源，都给你安排好啦 🐾

[![Release](https://img.shields.io/github/v/release/kankejiang/CatClawVideo)](https://github.com/kankejiang/CatClawVideo/releases/latest)
[![License](https://img.shields.io/github/license/kankejiang/CatClawVideo)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android-blue)](https://github.com/kankejiang/CatClawVideo/releases/latest)
[![QQ 交流群](https://img.shields.io/badge/QQ%E7%BE%A4-855383639-blue)](https://qm.qq.com/q/Fhu3IEzqa4)

</div>

---

## Welcome

- 猫爪影视是基于 .NET MAUI 的跨平台影视播放器
  - A modern cross-platform video player built with .NET MAUI

## Feature

- **多源聚合**
  - 猫爪源（本地 JSON 源文件）+ TVBox / 影视仓订阅（MacCMS JSON 站点、spider 爬虫站点），多源统一浏览与跨源搜索
- **订阅解析**
  - TVBox 明文 JSON 配置解析，加密源三重解密通道（图片尾部隐写、饭太硬官方解密接口、明文直读）
- **爬虫运行时**
  - `drpy2` JS 脚本爬虫（内置 Jint JS 引擎）；Java jar/dex 爬虫（`csp_Xxx`，桌面端经 JavaBridge 加载）
- **双端播放器**
  - Android 使用 Media3 ExoPlayer（含 HLS），Windows 使用 WinUI MediaPlayer
- **完善的媒体库**
  - 播放历史、收藏、本地媒体；内置 BitTorrent 下载与流式缓存
- **检查更新**
  - 关于页一键检查更新，弹窗展示更新日志与平台安装包直链

## Quick Start

前往 [Release](https://github.com/kankejiang/CatClawVideo/releases/latest) 页面下载最新版本：

| 文件 | 平台 | 说明 |
|------|------|------|
| `catclaw.video-x.y.z-Setup.exe` | Windows | 安装版（self-contained，无需另装 .NET 运行时） |
| `com.catclaw.video-Signed.apk` | Android | arm64-v8a 签名包，Android 12 (API 31) 及以上 |

**首次使用**：在 App「源配置」页粘贴猫爪源文件路径或 TVBox 订阅地址即可添加片源，示例源文件见 [samples/](samples/)。

> 本项目仅为播放器壳，不内置任何片源，也不提供、不存储、不上传任何影视内容。所有源均由用户自行配置，用户需对所添加的源及观看内容承担全部责任。本项目与 TVBox / 影视仓及其他第三方源无隶属关系。

## Link

| QQ Group | [![QQ 交流群](https://img.shields.io/badge/QQ%E7%BE%A4-855383639-blue)](https://qm.qq.com/q/Fhu3IEzqa4) |
|:-:|:-:|
| 相关项目 | [![猫爪音乐](https://img.shields.io/badge/%E7%8C%AB%E7%88%AA%E9%9F%B3%E4%B9%90-CatClawMusic-pink)](https://github.com/kankejiang/CatClawMusic) |

使用问题、片源配置、功能建议都欢迎进群交流；也可以在仓库 [Issues](https://github.com/kankejiang/CatClawVideo/issues) 中反馈。

---

## 猫爪源协议 (CatClaw Source v1)

本地 JSON 源文件格式，示例见 [samples/catclaw-source.sample.json](samples/catclaw-source.sample.json)：

```json
{
  "magic": "catclaw-source",
  "version": 1,
  "name": "源名称",
  "categories": [ { "id": "demo", "name": "分类" } ],
  "items": [
    {
      "id": "v001",
      "title": "片名",
      "category": "demo",
      "sources": [
        {
          "name": "线路名",
          "episodes": [ { "name": "正片", "url": "https://.../master.m3u8" } ]
        }
      ]
    }
  ]
}
```

在 App「源配置」页粘贴本地文件路径即可添加。

## 项目结构

| 目录 | 说明 |
|------|------|
| `CatClawVideo.Core` | 核心库：模型、接口、片源提供者（猫爪源 / MacCMS / spider 爬虫运行时）、TVBox 订阅解析 |
| `CatClawVideo.Data` | 数据层：SQLite 数据库（订阅源、播放历史、收藏） |
| `CatClawVideo.Maui` | MAUI 应用主体：页面、ViewModel、平台播放器 Handler |
| `JavaBridge` | 桌面端 Java 桥（android-stub + 爬虫桩 + bridge server → `bridge.jar`），用于在桌面加载 TVBox jar 爬虫；`vendor/unidbg` 为 Guard 加固包解壳器（模拟 ARM64 跑原生解密 .so） |
| `samples/` | 猫爪源协议示例文件 |
| `prototype/` | HTML 界面原型 |

## 构建

### 环境要求

- .NET SDK **11.0.100-rc.1**（见 [global.json](global.json)）
- MAUI 工作负载（`dotnet workload install maui-android`）
- Android：API 36 平台，运行时标识 `android-arm64` / `android-x64`
- Windows：WinAppSDK 1.7，10.0.19041.0 以上
- JavaBridge：JDK 17+（构建 `bridge.jar`）；Guard 解壳器需 JDK 17+ 运行（`JavaBridge\build-unidbg.cmd` 构建 `vendor\unidbg\unpacker.jar`）

### 构建 App

```powershell
# Android（Debug 直接运行即可）
dotnet build CatClawVideo.Maui/CatClawVideo.Maui.csproj -f net11.0-android

# Windows
dotnet build CatClawVideo.Maui/CatClawVideo.Maui.csproj -f net11.0-windows10.0.19041.0
```

> 说明：Windows Release 通过手写 `App.generated.cs` 与 XamlCompilerWrapper / MakePriWrapper 绕过 .NET 11 + WinAppSDK 的编译器问题（详见 csproj 注释）；Android Release 已关闭 AOT / R2R / 裁剪，以兼容爬虫运行时的动态代码加载。

## Thanks

- [猫爪音乐 CatClawMusic](https://github.com/kankejiang/CatClawMusic) 同作者的跨平台音乐播放器，UI 与工程实践一路互相喂招
- TVBox / 影视仓生态的订阅与爬虫协议，让「源」这件事有了通用语言
- [Jint](https://github.com/sebastienros/jint) 让 JS 爬虫在 C# 里跑起来的 JS 引擎
- 不过最最重要的，还是需要感谢屏幕前的你哦~

---

## License

[MIT](LICENSE) © 2026 kankejiang
