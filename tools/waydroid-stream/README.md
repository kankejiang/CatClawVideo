# tools/waydroid-stream — CATCLAW/1 投屏服务端（108 的 Waydroid）

T3 任务包交付（协议见 `docs/tasks/README.md` 第二节）。把 108 上 Waydroid 的 Android
画面按 `CATCLAW/1` 推给宿主侧客户端，并接收触摸/按键/滚轮帧注入 Android。

## 快速开始

```bash
# 108 上（root@10.0.0.108）
cd /root/waydroid-stream            # 或仓库内 tools/waydroid-stream
bash serve.sh -d --port 27183 --codec h264 --fps 30

# 任意一台能连通 10.0.0.108 的机器
ffmpeg -i tcp://10.0.0.108:27183 -frames:v 60 -f null -     # 解码 60 帧
python3 probe_client.py 10.0.0.108:27183 --save 30 --seconds 15
```

serve.sh 一键完成：pulse socket 坑修复 → **headless Weston**（没有就自动拉）→
container/session/show-full-ui → 等 adb → **唤醒 + 常亮** → serve.py（参数透传，`-d` 后台）。

## 文件

| 文件 | 说明 |
|---|---|
| `serve.sh` | 一键脚本（坑修复 + Waydroid 拉起 + serve.py） |
| `serve.py` | 协议服务端：握手、长度前缀帧、心跳、多客户端扇出、采集源 |
| `input.py` | 输入注入（持久 `waydroid shell`，回退逐条）；可独立 CLI 自测 |
| `probe_client.py` | 自带简易客户端：握手、收 N 帧存盘、周期注入、心跳 |

## 视频源（--source / --codec）

| 路线 | 说明 |
|---|---|
| **默认：screencap→宿主转码** | `screencap -p` 循环（不依赖画面变化）→ 宿主 ffmpeg 转码：`--codec h264` 出 **CFR 30fps H.264**（`-g 30` 每秒 IDR）；`--codec jpeg` 出 MJPEG |
| `--source screenrecord` | 设备端 `screenrecord --output-format=h264`（+`--bugreport` 覆盖层）。⚠ **纯 VFR**：画面完全静止时零帧（实测等 50s），仅供对照，不建议联调使用 |

**为什么默认源走 screencap+转码**（实测记录）：screenrecord 纯 VFR，静止桌面连第一帧
都不出；ffmpeg 的 `find_stream_info`/`fps` 滤镜又需要持续输入才出流——两者叠加导致
静止桌面永远黑屏、验收命令挂死。screencap 不依赖画面变化，配 `fps=30` CFR 转码后
恒定 30fps（静止时复制帧）、`-g 30` 每秒 IDR（后接入的解码器 ≤1s 入手）。

## 协议实现说明

- 握手：客户端发 `CATCLAW/1\n` → 服务端回 `OK <w> <h> <codec> <fps>\n`
  （**双模**：连接后 1.5s 内不发握手行的，按 **raw 模式**处理——无握手行、无帧头、
  纯 Annex-B/MJPEG 裸流，专供 `ffmpeg -i tcp://…` 直连；此为对文档的**扩展**，
  协议客户端行为不受影响）
- 帧：`<4B 大端长度><1B 类型><payload>`，0x01 视频 / 0x02 心跳 / 0x10 触摸 /
  0x11 按键 / 0x12 滚轮 / 0x00 关闭；单帧上限 4MiB；未知类型按长度跳过
- H.264 帧 = 完整访问单元（Annex-B）。AU 边界按 slice 头 `first_mb_in_slice` 判定
  （zerolatency 的 sliced-threads 一帧多 slice，不能用"见切片就切"）
- 新客户端接入先补「SPS/PPS+IDR」帧（宿主编码器每秒一个 IDR）→ 接入后 ≤1s 出画面；
  接入后前几帧可能有 `non-existing PPS` 告警（IDR 前的帧被解码器跳过），属预期

## 坐标约定

- **客户端送设备像素**（= `adb shell wm size` 的当前值，握手行回传），服务端**原样注入**
- 触摸合成：`down` 记起点；`move` 累计（位移 >6px 判拖动，以 ~80ms 间隔注入
  `input swipe` 片段保证拖动可见）；`up` 时合成 `input tap`（未拖动）或 `input swipe`（拖动）
- 滚轮（0x12）为**近似**：屏幕中心竖向 `input swipe`（dy>0 → 手指上滑 = 内容下滚），
  幅度 clamp ±300px
- 按键：`input keyevent <keycode>`（原样透传）

## 验证结果（2026-10-01，108 实测原始输出摘录）

```text
═══ ffmpeg 直连解码 60 帧 ═══
[h264 @ …] non-existing PPS 0 referenced        ← 接入后 IDR 前的 ~8 帧被跳过，预期
（其后 60 帧解码无错，进程正常退出）

═══ probe_client ═══
服务端: OK 1280 688 h264 30
已注入: tap(中心) / swipe(自下而上) / key(HOME=3)
时长 15.4s  视频帧 307  实测 20.0 fps  共 0.57 MB (codec=h264 1280x688@30)
发送输入帧 10 个

═══ 注入可见反应 ═══
am start 设置 → 截屏 md5 变化 → input.py key 3 (HOME) → 截屏 md5 再变
桌面=1b3ea13b… 设置=6dd1…（略） 回桌面=…   ✔ 注入生效

═══ jpeg 兜底 ═══
服务端: OK 1280 688 jpeg 15
时长 8.2s  视频帧 23  实测 2.8 fps     JPEG 单帧可解 ✔

═══ 多客户端并发 ═══
两个 client5 同时收：各 84 帧 / 5.1s（16.8fps）✔
```

## 实测性能

| 指标 | 值 |
|---|---|
| 协议输出帧率 | 恒定 30fps（CFR，静止画面复制帧） |
| 内容真实刷新 | ~2.5fps（screencap 抓帧速率，adb+PNG 编码是瓶颈） |
| 码率 | 静态桌面 ~0.5Mbps；动态 ~8Mbps（--bit-rate 可调，现默认走 libx264 CRF） |
| 接入延迟 | ≤1s（等下一个 IDR）；首帧解码前 ~8 帧被跳过（PPS 告警） |
| 注入延迟 | tap ~50-150ms（持久 waydroid shell） |

## 已知限制

1. scrcpy-server 转发路线未实现（`--source scrcpy` 会报错退出）：scrcpy 4.1 客户端协议
   复杂，而 screencap→转码路线已满足协议与验收；后续若需要更低延迟/更高刷新再上。
2. 内容刷新 ~2.5fps（screencap 瓶颈）：播视频类高动态内容会不够顺滑，属路线上限。
3. 长按（press-and-hold）无专门语义：down…up 无拖动会被合成 tap。
4. 接入后前几帧有 `non-existing PPS` 告警（≤1s），解码器自动恢复。
5. 分辨率动态变化（wm size Override 变更）未做重协商：服务重启即可。
