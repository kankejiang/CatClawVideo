# stream-probe — CATCLAW/1 投屏客户端（T2 任务包）

独立控制台工具（C#，`net11.0-windows` + WinForms），实现 [CATCLAW/1 协议](../../docs/tasks/README.md) 的
**Windows 侧客户端**：连接 → 握手 → 收流解码显示 → 鼠标/键盘/滚轮回传。
自带 `--mock` 模拟服务端，**完全自测、不依赖 T1/T3**。

> 本目录为独立 csproj，**不在 .sln 内**，不依赖 `CatClawVideo.Maui` / `CatClawVideo.Core`。

## 构建 & 运行

```powershell
# 构建（可选，dotnet run 会自动构建）
dotnet build tools\stream-probe\stream-probe.csproj

# 自测：终端 A 起模拟服务端
dotnet run --project tools\stream-probe -- --mock --port 27183 --verbose

# 自测：终端 B 起客户端连上去
dotnet run --project tools\stream-probe -- --connect 127.0.0.1:27183
```

客户端窗口显示 640×360 合成画面（弹跳色块 + 帧号 + 状态行）。
在窗口内**点击 / 拖动 / 滚轮 / 按键**，模拟服务端会打印收到的输入帧。

### 命令行参数

| 参数 | 说明 |
|---|---|
| `--mock` | 启动模拟服务端（合成 JPEG 30fps + 打印输入帧） |
| `--connect host:port` | 作为客户端连接服务端（默认端口 27183） |
| `--port N` | `--mock` 模式监听端口（默认 27183） |
| `--dump N` | 把收到的前 N 个视频帧存为 `dump\frame_%04d.jpg` |
| `--auto-input` | 连接后 2s 自动发一组触摸/按键/滚轮帧（无人值守自测用） |
| `--seconds N` | N 秒后自动退出（无人值守自测用） |
| `--verbose` | 打印前若干帧的帧头（长度/类型）与心跳 |

### 输入映射

| 窗口操作 | 协议帧 |
|---|---|
| 鼠标左键 按下/抬起/移动 | `0x10` 触摸（坐标按 Zoom 模式 letterbox 反算到服务端分辨率，clamp 到 u16） |
| 鼠标右键 | `0x11` 按键 keydown+keyup，keycode=`BACK(4)` |
| 滚轮 | `0x12`（Delta clamp 到 i16；正负即上下滚） |
| 键盘 | `0x11` 按键，keycode 见下（方向键/回车/ESC/空格/退格/Tab/字母/数字） |

### 键码语义（协议留白，建议补协议）

CATCLAW/1 协议只定义了 `0x11` 帧 = `u32 keycode + u8 action`，**未定义 keycode 的取值语义**。
本工具统一采用 **Android keycodes**（`AndroidKeys.cs` 内置子集：`BACK=4`、`DPAD_*=19..23`、
`VOL+/-=24/25`、`POWER=26`、`ENTER=66`、`ESC=111`、`A=29..Z=54`、`0=7..9=16` …），
这样 T3 注入端可直接透传给 `input keyevent <keycode>`，零转换。

> **协议建议**（按 T2 任务书要求在此提出，不改 `docs/tasks/README.md`）：
> 把「keycode 采用 Android 键码（frameworks/base `KeyEvent.java` 别名表）」补进协议文档，
> 由维护者定稿后 T2/T3 双方对齐。

## 已验证项（mock+connect 双进程自测，2026-10-01）

```powershell
# 模拟服务端
stream-probe.exe --mock --port 27183 --verbose
# 客户端（无人值守）
stream-probe.exe --connect 127.0.0.1:27183 --dump 30 --auto-input --seconds 12 --verbose
```

- **握手**：客户端发 `CATCLAW/1\n`，服务端回 `OK 640 360 jpeg 30` ✔
- **收流帧率**：`[client] 结束：共收视频帧 362，实测显示帧率 29.5fps`（12s，≥25fps 达标）✔
- **窗口显示**：真实窗口截图确认移动色块/帧号/状态行正常渲染，标题栏实时帧率 `30.0fps frame=121` ✔
- **输入回传**（`--auto-input`，mock 端原始输出，坐标已换算到服务端分辨率）：

  ```
  [输入] 触摸 down @ (160,90)
  [输入] 触摸 move @ (320,180)
  [输入] 触摸 move @ (480,270)
  [输入] 触摸 up @ (480,270)
  [输入] 按键 down keycode=29 (A)
  [输入] 按键 up keycode=29 (A)
  [输入] 按键 down keycode=22 (DPAD_RIGHT)
  [输入] 按键 up keycode=22 (DPAD_RIGHT)
  [输入] 滚轮 dx=0 dy=-120（上滚）
  [输入] 滚轮 dx=0 dy=120（下滚）
  ```
- **dump**：`--dump 30` 落盘 30 张 `frame_*.jpg`，图片查看器可打开，内容与合成画面一致 ✔
- **心跳**：双向 8B 大端单调毫秒，`--verbose` 可见 ✔
- **边界**：未知类型按长度跳过不崩；单帧 >4MiB 断开报错；半包/粘包 ReadExact 循环补齐；握手行容忍 `\r` ✔

## 未验证项 / 已知限制

- **H.264 解码（`codec=h264`）为桩**：`H264Decoder.cs` 目前返回 false（每 100 帧提示一次）。
  计划用 Media Foundation `MFCreateTransform` 实现；若 MF 做不动则 `ffmpeg.exe` 子进程兜底
  （届时会在本 README 声明该外部依赖）。**jpeg 链路已完整验证**，h264 待 T3 服务端就绪后联调。
- **右键 = BACK** 是本工具的映射约定（模拟遥控返回），非协议内容；联调时可按需调整。
- 触摸坐标按 **Zoom（保持比例 letterbox）** 反算：窗口黑边区域点击会 clamp 到边缘坐标。

## 文件结构

```
tools/stream-probe/
  stream-probe.csproj   # 独立工程：net11.0-windows + UseWindowsForms，不进 .sln
  Program.cs            # 命令行解析、客户端会话（收流/dump/auto-input/退出码）
  Protocol.cs           # CATCLAW/1 编解码：握手 + 长度前缀帧 + 各类型负载（全部大端）
  AndroidKeys.cs        # Android 键码子集 + WinForms Keys → keycode 窄映射
  MockServer.cs         # --mock：30fps 合成 JPEG + 输入帧打印 + 心跳
  ViewWindow.cs         # WinForms 显示（双缓冲）+ 鼠标/键盘/滚轮捕获 → 输入帧
  H264Decoder.cs        # Media Foundation 解码桩（TODO：待 T3 联调时实现）
```
