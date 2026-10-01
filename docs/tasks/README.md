# 并发开发总纲（B1 拆分：4 个独立任务包）

> 目的：把 B1（真 Android + 宿主只做显示）剩余工作拆成**互不冲突**的四条线，可在不同 harness 上并行推进。

## 一、四条线一览

| 编号 | 任务 | 交付物 | 依赖 | 预估 |
|---|---|---|---|---|
| **T1** | QEMU guest 显示栈收敛（SurfaceFlinger 起来 + `screencap` 出图） | guest 内 SF 稳定运行、`screencap` 得到 PNG | 无（自成一体） | 不确定（每轮消灭一个原因） |
| **T2** | 投屏**客户端**（Windows/宿主侧） | 独立控制台工具 `tools/stream-probe`：连上流、解码、显示、回传输入；用**本地模拟服务端**自测 | 只依赖本文件的协议 ✔ | 2~3 天 |
| **T3** | 投屏**服务端**（Android 侧：108 上的 Waydroid 或 T1 的 guest） | `tools/waydroid-stream`：把 Android 画面按协议推流 + 接收输入注入 | 需要一台 Linux（108） | 2~3 天 |
| **T4** | **P2 框架层路线预研**（zygote / system_server 可行性） | `tools/framework-probe/**` + `docs/research/framework/**`：证据化差距矩阵、三条路线对比与推荐、只读探针 | 只读分析（不改 guest ✔） | 2~3 天 |

**关键**：T2 与 T3 **只通过下面的协议耦合** ✔ —— 各自先用**模拟对端**自测，最后联调 ✔。
T4 是**只读预研** ✗（不碰 guest 镜像 ✗），为 P2（唯一的大块未知 ✗）提前给出决策与差距 ✔。

## 二、共享协议 `CATCLAW/1`（TCP，默认端口 **27183**）

```
1) 客户端连上后发一行（\n 结束，ASCII）：
     CATCLAW/1
2) 服务端回一行：
     OK <width> <height> <codec> <fps>
     codec ∈ { h264, jpeg }
3) 之后双向发送**长度前缀帧**：
     <4B 大端长度 length><1B 类型 type><length 字节负载>
     · 0x01 视频帧（h264=Annex-B 访问单元；jpeg=完整 JPEG 字节）
     · 0x02 心跳（负载 8B 大端 = 单调毫秒时间戳）
     · 0x10 触摸（负载 5B：u16 x, u16 y, u8 action；action: 0=down,1=up,2=move）
     · 0x11 按键（负载 5B：u32 keycode, u8 action；action: 0=down,1=up）
     · 0x12 滚轮（负载 4B：i16 dx, i16 dy）
     · 单帧上限 4 MiB；未知类型必须**跳过**（按长度丢弃）
4) 任何一方可发送 0x00 关闭（负载空），随后关闭连接。
```

**模拟对端（两边都要写一个，便于各自自测）**
- 简易服务端（T2 用）：按协议推 30fps 的**合成 JPEG**（例如 640×360 上画移动色块），并把收到的输入帧**打印**出来。
- 简易客户端（T3 用）：连上后按协议收流、存成文件，并周期性发几个 0x10/0x11 帧。

## 三、通用约定（三条线都遵守）

1. **仓库与构建**
   - 仓库：`D:\Code\CatClawVideo`（Windows）；`108` 为 `root@10.0.0.108`（Debian，可免密 ssh）。
   - 宿主主程序构建：`dotnet build CatClawVideo.Maui\CatClawVideo.Maui.csproj -f net11.0-windows10.0.26100.0`
   - guest 镜像重建（**只有 T1 用**）：`cmd /c .zwork\rebuild_gb.cmd`（含镜像自检 12 项）
2. **提交规范**：中文 Conventional Commits，格式 `type(scope): 现象——根因`；
   多行/带引号的消息**写文件再 `git commit -F`** ✔（PowerShell 直接传参会炸 ✗）。
3. **边界纪律（重要）**
   - **只改自己任务范围内的文件**：T1=`JavaBridge/qemu-src/**` + `JavaBridge/src/bridge/Server.java`；
     T2=`tools/stream-probe/**`（新建）；T3=`tools/waydroid-stream/**`（新建）＋ `108` 上的系统配置。
   - 工作区里存在**其它会话的在飞改动**（`CatClawVideo.Core.csproj`、`JavaSpiderRuntime.cs`、`SpiderUiHost.cs`、
     `bridge.jar`、`gb.dex`、`proppreload.*`、`ui_stub.dex`）⇒ **不要提交它们** ✔，也不要覆盖 ✗。
   - 不要动 `CatClawVideo.Maui` 的现有页面/VM ✗（T2 先做成**独立控制台工具** ✔，联调阶段再谈接入）。
4. **PowerShell 坑（本会话踩过多次）**
   - `${var}` / 嵌套引号 / `2>` / `|` 在 `adb shell "..."`、`ssh '...'` 里极易被吃掉 ✗
     ⇒ **把远端命令写成脚本文件再 scp 过去执行** ✔。
   - `.Replace(a,b,1)` 在 PS 里不存在 ✗（只有 2 参）⇒ 用 `edit` 工具或 Python 脚本做文本替换 ✔。
   - `bytes` 字面量不能含中文 ✗ ⇒ 用 `.encode("utf-8")` ✔。
5. **验收要有实证**：每条任务完成时必须给出**可复现命令 + 原始输出**（不要只写"应该可以"✗）。

## 四、可复用的既有设施（三条线都可能用到）

| 设施 | 位置/用法 |
|---|---|
| guest adb 直连 | 应用启动后 `%APPDATA%\CatClawVideo.debug\home-debug.log` 里有 `adb 隧道已开：adb connect 127.0.0.1:<port>`；然后 `adb -s 127.0.0.1:<port> shell …` |
| guest 控制台日志 | `%LOCALAPPDATA%\CatClawVideo.debug\qemu-console-art.log`（含 guest 内核/serial 输出） |
| guest 崩溃现场 | `adb shell cat /data/crash.log`（信号、回溯、`/proc/self/maps` 基址） |
| 属性 shim | guest 内 `/system/lib64/libpropfix.so`（`PROPFIX="k=v;k=v"`，`PROPFIX_DEBUG=1` 开日志，`PROPFIX_CRASH=1` 装崩溃处理器） |
| EGL 探针 | guest 内 `/system/bin/eglprobe`（打印 EGL 版本/配置数） |
| 服务清单 | guest 内 `/system/bin/svccheck`（查 servicemanager 里 AIDL 服务是否注册） |
| 108 上已跑通的 Android | Waydroid 1.6.2（`systemctl restart waydroid-container` → `waydroid session start` → `waydroid show-full-ui`）+ 我们的 APK/数据 |
