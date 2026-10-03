# CatClawVideo.Stream｜投屏协议库 + 宿主使用说明

从 T2 的 `tools/stream-probe/Protocol.cs` 逐字复制出来的 `CATCLAW/1` 协议客户端，
加上自动重连、编码优选和统计，供 `CatClawVideo.Maui` 的「投屏」页使用。
抽件清单与 h264 接缝见 `docs/tasks/N4-1-抽件通知.md`。

## 三步看到画面（Windows 端不需要装任何环境）

前提只有一条：**108 那台机上有 jpeg 服务端在听**（服务端由 T3 的 `tools/waydroid-stream/` 提供）。

1. **起服务端**（在 108 上，一次性）
   ```bash
   bash serve.sh -d --port 27183 --codec jpeg --fps 15
   ```
   ⚠ `serve.sh` 会**重启 waydroid 容器**：如果那台机上别人正在看流，别用它，
   直接起 `python3 serve.py --port 273xx --codec jpeg --fps 15`（换自己的端口）。
   宿主页上的「帮我拉起服务端」按钮走的就是后者（`serve.py`，不碰容器）。
2. **双击** `CatClawVideo.Maui\build\...\CatClawVideo.Maui.exe`（单文件，自包含，免装 .NET）。
3. 首页右上角点 **「投屏」** → 确认地址（默认 `10.0.0.108`，端口按 27383/27183 顺序探）→ 点 **「一键连接」**。

进页面时它会自己探一次端口，探测行直接写清结果，例如
`27383 在线 1280x688 jpeg@15 可解码=是`；连不上时给的是能照着做的中文提示，不是异常堆栈。

## 操作对照

| 输入 | 效果 |
|---|---|
| 左键点 / 按住拖 | 安卓触摸 down / move / up（拖动即滑动） |
| 滚轮 | 滚轮帧 `0x12` |
| 右键 / 中键 | 安卓 BACK / HOME |
| 方向键、回车、空格、Tab、PgUp/PgDn、Home/End、Del、A–Z、0–9 | 对应 Android keycode |
| `Esc` | **退出本页**（不发给安卓；要安卓返回用右键） |

状态栏实时显示：连接状态、实测 fps、收流→上屏延迟（均值/峰值）、丢帧、码率、
`地址:端口 编码 宽x高@帧率`、HiDPI 缩放比、画面 DIP 尺寸。

## 排障

- **画面黑、状态栏"断线，退避重连中 …"**：服务端被重启了。客户端按 0.5/1/2/4/8/16/30 s 退避一直重试，
  服务端回来就自动续上，不用手点。
- **提示"连不上可解码的投屏服务端"**：服务端在跑但只出 h264，而本机解码器还没落地 ⇒ 换 `--codec jpeg`。
- **地址被填坏**（粘进两个地址、带空格中文）：读回时按 IPv4 结构校验，非法就退回默认值，
  不会拿脏值去查 DNS 再报一句看不懂的"不知道这样的主机"。
- 详细日志：`%APPDATA%\CatClawVideo.debug\home-debug.log`，投屏相关行都带 `[remote]` 前缀，
  每次点击会留一行 `输入 dip=… rect=… -> 设备=…`，坐标对不对可以直接对服务端日志。

## 无头长跑台架（不需要界面）

```bash
dotnet build CatClawVideo.Stream/Harness/Harness.csproj -c Release
./CatClawVideo.Stream/Harness/bin/Release/net11.0/stream-harness.exe \
    --host 10.0.0.108 --ports 27384 --seconds 1800 --csv soak.csv
```
每 5 秒一行 CSV：`t_s,phase,fps_recv,frames,bytes,mbps,interval_ms,maxgap_ms,reconnects,shown,dropped,lat_avg_ms,lat_max_ms,privMB,badPayload`。
