# T2｜投屏**客户端**（Windows / 宿主侧）

> 独立任务包。**只新建** `tools/stream-probe/**`（不要动 `CatClawVideo.Maui` 的现有页面/VM ✗，
> 那里有其它会话的在飞改动）。协议见 `docs/tasks/README.md` 第二节，通用约定见第三节。

## 一、目标

做一个**独立控制台工具**（C#，`net11.0-windows`），实现协议 `CATCLAW/1` 的**客户端**：
1. 连接 `host:port`（默认 `127.0.0.1:27183`），按协议握手、收流；
2. **解码并显示**：`codec=jpeg` 时直接解 JPEG 显示；`codec=h264` 时用 **Media Foundation** 解 H.264 并显示；
3. **回传输入**：把窗口里的鼠标左键/右键/滚轮/键盘映射成 `0x10/0x11/0x12` 帧发回；
4. **自带模拟服务端**（`--mock`）：按协议推 30fps **合成 JPEG**（例如 640×360 上移动的色块 +
   居中文字显示当前帧号），并把收到的输入帧**打印**出来 ⇒ **完全自测、不依赖 T1/T3** ✔。

## 二、验收（必须贴原始输出）

```powershell
# 1) 自测：模拟服务端 + 客户端（一条命令里两个窗口/两个进程）
dotnet run --project tools\stream-probe -- --mock --port 27183
dotnet run --project tools\stream-probe -- --connect 127.0.0.1:27183
```
- ✅ 通过 = 窗口里能看到**移动色块**与帧号（≥25fps，无明显撕裂）；在窗口里点击/滚轮/按键时，
  模拟服务端**打印出对应的输入帧**（类型/坐标/键码正确，字节序正确）
- ✅ 再加一条：`--dump 100` 模式把收到的 100 帧存成 `frame_*.jpg`，可用图片查看器打开

**联调（可选，等 T3 就绪）**
```powershell
dotnet run --project tools\stream-probe -- --connect 10.0.0.108:27183
```

## 三、实现要求（避免返工）

1. **协议严格按 README**：长度前缀（4B 大端）、类型字节、未知类型**按长度跳过** ✗ 不要崩 ✗。
2. **端口/地址可配**：`--port` / `--connect host:port`；`--mock` 模式不要依赖任何外部服务 ✔。
3. **显示**：用 WinForms/WPF 均可，但**不要**引入到 `CatClawVideo.Maui` 的依赖里 ✗（保持独立 csproj ✔）；
   `tools/stream-probe/stream-probe.csproj` 自带 `net11.0-windows` TFM ✔（**不要加进 .sln** ✗，避免与其它会话冲突 ✗）。
4. **H.264 解码**：优先 **Media Foundation**（`Windows.Media` / `MFCreateTransform` 均可）；
   若一时做不出来，可先用 `ffmpeg.exe` 子进程兜底 ✗（但要在 README 里写清依赖 ✗）。
5. **输入注入方向**：本工具只负责**把事件发出去** ✔；真正的注入在 T3（Android 侧 `input`）✔。
6. **日志**：协议收发要能 `--verbose` 打印前 N 帧的头部（长度/类型/时间），便于联调排错 ✔。

## 四、目录建议

```
tools/stream-probe/
  stream-probe.csproj        # 独立，net11.0-windows，不进 .sln
  Program.cs                 # 命令行解析 + 主循环
  Protocol.cs                # CATCLAW/1 编解码（握手 + 长度前缀帧）
  MockServer.cs              # --mock：合成 JPEG 30fps + 打印收到的输入
  ViewWindow.cs              # 显示 + 鼠标/键盘捕获 → 输入帧
  H264Decoder.cs             # Media Foundation 解码（可留 TODO 桩）
  README.md                  # 用法、已测项、未测项、联调注意
```

## 五、命令速查

```powershell
# 新建（不要用 dotnet new 覆盖仓库根，直接建目录 + 文件）
mkdir tools\stream-probe
# 运行
dotnet run --project tools\stream-probe -- --mock --port 27183 --verbose
dotnet run --project tools\stream-probe -- --connect 127.0.0.1:27183
```
**生成合成 JPEG** 可用 `System.Drawing`（`net11.0-windows` 下可用）✔。

## 六、已知坑

1. **协议字节序**统一**大端** ✗（Java/Android 侧也用大端 ✔），别混用小端。
2. 一帧最大 **4 MiB** ✗：超了直接断开并报错 ✗（避免内存失控 ✗）。
3. 半包/粘包**必须**正确处理 ✗（TCP 是字节流 ✗ ⇒ 用固定缓冲累积 ✗，别假设一次 `Read` 一帧 ✗）。
4. 大小写/换行：握手行以 **`\n`** 结束 ✗（不是 `\r\n` ✗），解析时容忍 `\r` ✔。
5. 别把 `--mock` 写成"连不上就假装成功" ✗——必须真的按协议跑通端到端 ✔。

## 七、交付

- `tools/stream-probe/**` 全部新文件 + 提交（中文 Conventional Commits）✔
- `tools/stream-probe/README.md` 写清：用法、已验证项（含原始输出）、未验证项（如 H.264 待联调）✔
- **不要**修改 `docs/tasks/README.md` 里的协议定义 ✗；若发现协议需要改，**在 PR/提交说明里提出** ✔（T3 依赖它 ✔）
