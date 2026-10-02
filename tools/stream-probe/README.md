# stream-probe — CATCLAW/1 投屏客户端（T2 任务包）

独立控制台工具（C#，`net11.0-windows` + WinForms），实现 [CATCLAW/1 协议](../../docs/tasks/README.md) 的
**Windows 侧客户端**：连接 → 握手 → 收流解码显示 → 鼠标/键盘/滚轮回传。
自带 `--mock` 模拟服务端，**完全自测、不依赖 T1/T3**。

> 本目录为独立 csproj，**不在 .sln 内**，不依赖 `CatClawVideo.Maui` / `CatClawVideo.Core`。

## 一键投屏（N2-3：三步看到 108 的安卓画面）

1. **双击 `launch-cast.cmd`**（首次运行会自动编译）
2. 等待窗口弹出（服务端没起会**自动 ssh 拉起** 108:/root/waydroid-stream 并等源预热）
3. 看画面 —— 鼠标点击/拖动=触摸，右键=BACK，滚轮=滚动；**Esc 退出**；断线自动重连

前提（一次性）：`ssh root@10.0.0.108` 免密可登录、108 上存在 `/root/waydroid-stream/serve.sh`。
窗口按设备宽高比自适应，标题栏实时显示帧率与累计帧数。

## 构建 & 运行

```powershell
# 构建（可选，dotnet run 会自动构建）
dotnet build tools\stream-probe\stream-probe.csproj

# 自测：终端 A 起模拟服务端
dotnet run --project tools\stream-probe -- --mock --port 27183 --verbose

# 自测：终端 B 起客户端连上去
dotnet run --project tools\stream-probe -- --connect 127.0.0.1:27183

# 真机投屏（108 的 waydroid）
dotnet run --project tools\stream-probe -- --connect 10.0.0.108:27283
```

客户端窗口显示 640×360 合成画面（弹跳色块 + 帧号 + 状态行）。
在窗口内**点击 / 拖动 / 滚轮 / 按键**，模拟服务端会打印收到的输入帧。

### 命令行参数

| 参数 | 说明 |
|---|---|
| `--mock` | 启动模拟服务端（合成 JPEG 30fps + 打印输入帧） |
| `--connect host:port` | 作为客户端连接服务端（默认端口 27183） |
| `--port N` | `--mock` 模式监听端口（默认 27183） |
| `--dump N` | 把收到的前 N 个视频帧存为 `dump\frame_%04d.jpg/png` |
| `--auto-input` | 连接后 2s 自动发一组触摸/按键/滚轮帧（无人值守自测用） |
| `--seconds N` | N 秒后自动退出（无人值守自测用） |
| `--verbose` | 打印前若干帧的帧头（长度/类型）与心跳 |
| `--save-stream 文件` | 连接后不发握手（raw 裸流模式）抓 T3 的 Annex-B 流存盘 |
| `--decode-file 文件` | 离线解码统计：AU 数/解码率/首帧延迟/单帧耗时（N2-1 判据） |
| `--width N --height N` | `--decode-file` 时把握手分辨率作为解码器尺寸提示 |

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

- **右键 = BACK** 是本工具的映射约定（模拟遥控返回），非协议内容；联调时可按需调整。
- 触摸坐标按 **Zoom（保持比例 letterbox）** 反算：窗口黑边区域点击会 clamp 到边缘坐标。
- **显示帧率受 T3 源上限约束**：screencap 源采集 ~2.3fps → 宿主 CFR 复制后发布 ~14fps
  （设备独占时）；客户端显示 100% 收到的帧，解码本身无瓶颈（单 AU 均值 16ms）。
- 解码器初始化日志较多（槽位自检/哨兵），属刻意排障输出；初始化失败会直接抛错。

## 文件结构

```
tools/stream-probe/
  stream-probe.csproj   # 独立工程：net11.0-windows + UseWindowsForms，不进 .sln
  launch-cast.cmd       # 一键投屏：探活→ssh 自动拉起服务端→断线重连（N2-3）
  Program.cs            # 命令行解析、客户端会话（收流/dump/auto-input/退出码）
  Protocol.cs           # CATCLAW/1 编解码：握手 + 长度前缀帧 + 各类型负载（全部大端）
  AndroidKeys.cs        # Android 键码子集 + WinForms Keys → keycode 窄映射
  MockServer.cs         # --mock：30fps 合成 JPEG + 输入帧打印 + 心跳
  ViewWindow.cs         # WinForms 显示（双缓冲）+ 鼠标/键盘/滚轮捕获 → 输入帧；Esc=退出
  H264Decoder.cs        # Media Foundation 解码门面（N2-1 定稿，无外部依赖）
  MediaFoundationH264Decoder.cs # MF H264 解码实现（裸 vtable 直调 + 槽位哨兵自检）
```
