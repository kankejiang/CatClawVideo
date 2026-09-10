# 猫爪影视 (CatClawVideo)

跨平台影视播放应用，[猫爪音乐 (CatClawMusic)](https://github.com/kankejiang/CatClawMusic) 的姊妹项目。基于 .NET MAUI 构建，支持 Android 与 Windows，通过「猫爪源」本地源文件和 TVBox 订阅灵活扩展片源。

## 功能特性

- **多源聚合**：猫爪源（本地 JSON 源文件）+ TVBox / 影视仓订阅（MacCMS JSON 站点、spider 爬虫站点），多源统一浏览与跨源搜索
- **订阅解析**：TVBox 明文 JSON 配置解析，加密源三重解密通道（图片尾部隐写、饭太硬官方解密接口、明文直读）
- **爬虫运行时**：
  - `drpy2` JS 脚本爬虫（内置 Jint JS 引擎）
  - Java jar/dex 爬虫（`csp_Xxx`，桌面端经 JavaBridge 加载）
- **播放器**：Android 使用 Media3 ExoPlayer（含 HLS），Windows 使用 WinUI MediaPlayer；支持播放历史、收藏、本地媒体
- **界面**：海洋蓝 + 深色主题，底部导航，首页无限滚动加载

## 项目结构

| 目录 | 说明 |
|------|------|
| `CatClawVideo.Core` | 核心库：模型、接口、片源提供者（猫爪源 / MacCMS / spider 爬虫运行时）、TVBox 订阅解析 |
| `CatClawVideo.Data` | 数据层：SQLite 数据库（订阅源、播放历史、收藏） |
| `CatClawVideo.Maui` | MAUI 应用主体：页面、ViewModel、平台播放器 Handler |
| `JavaBridge` | 桌面端 Java 桥（android-stub + 爬虫桩 + bridge server → `bridge.jar`），用于在桌面加载 TVBox jar 爬虫 |
| `Tools/CatClawSourceGen` | 猫爪源生成器 CLI（试点：xb6v 磁力下载站抓取 → 猫爪源文件） |
| `sources/` | 预生成的猫爪源文件（`*.ccs.json`） |
| `samples/` | 猫爪源协议示例文件 |
| `prototype/` | HTML 界面原型 |

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

## 猫爪源生成器

```powershell
cd Tools/CatClawSourceGen
dotnet run -- --pages 3 --out xb6v.ccs.json   # 每分类抓 3 页
dotnet run -- --selftest 规则源.ccs.json      # 全链路自检（列表→详情→播放解析）
```

## 构建

### 环境要求

- .NET SDK **11.0.100-preview.7**（见 [global.json](global.json)）
- MAUI 工作负载（`dotnet workload install maui-android`）
- Android：API 36 平台，运行时标识 `android-arm64` / `android-x64`
- Windows：WinAppSDK 1.7，10.0.19041.0 以上
- JavaBridge：JDK 17+（构建 `bridge.jar`）

### 构建 App

```powershell
# Android（Debug 直接运行即可）
dotnet build CatClawVideo.Maui/CatClawVideo.Maui.csproj -f net11.0-android

# Windows
dotnet build CatClawVideo.Maui/CatClawVideo.Maui.csproj -f net11.0-windows10.0.19041.0
```

> 说明：Windows Release 通过手写 `App.generated.cs` 与 XamlCompilerWrapper / MakePriWrapper 绕过 .NET 11 + WinAppSDK 的编译器问题（详见 csproj 注释）；Android Release 已关闭 AOT / R2R / 裁剪，以兼容爬虫运行时的动态代码加载。

## 免责声明

本项目仅为播放器壳，不内置任何片源，也不提供、不存储、不上传任何影视内容。所有源均由用户自行配置，用户需对所添加的源及观看内容承担全部责任。本项目与 TVBox / 影视仓及其他第三方源无隶属关系。

## 许可证

[MIT](LICENSE) © 2026 kankejiang
