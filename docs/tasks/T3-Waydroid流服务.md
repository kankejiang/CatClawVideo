# T3｜投屏**服务端**（Android 侧：108 上的 Waydroid）

> 独立任务包。**只新建** `tools/waydroid-stream/**`（脚本 + 可选小程序）＋ `108`（`root@10.0.0.108`）上的系统配置。
> 不要改本仓库其它目录 ✗（尤其别碰 `CatClawVideo.Maui/**` 和 `JavaBridge/qemu-src/**` ✗）。
> 协议见 `docs/tasks/README.md` 第二节（**照它实现，别自创** ✔）。

## 一、背景（为什么选 108 的 Waydroid）

QEMU guest 的显示栈还在收敛（见 T1）✗；而 **108 上的 Waydroid 是现成可用的真 Android** ✔：
完整框架、我们的 APK 与 app 数据已装/已灌 ✔、显示正常 ✔。**先把"真 Android + 宿主只做显示"跑通** ✔，
等 T1 收敛后把同一个服务端实现搬到 guest 上即可（协议不变 ✔）。

108 环境（已确认）：Proxmox VE 宿主机、20 核/16G、Debian、KDE；Waydroid 1.6.2 已安装
（`/var/lib/waydroid/images/{system,vendor}.img`）、`binder_linux` 已加载、`/dev/kvm`、`/dev/dri/renderD128`(i915)。

## 二、目标与验收

**目标**：在 108 上提供一个**符合 `CATCLAW/1` 的流服务端**：
- 输出 Android 画面（**优先 H.264**；退而求其次 JPEG/MJPEG ✔）
- 接收 `0x10/0x11/0x12` 输入帧并**注入**到 Android（`input tap/swipe/keyevent` 或 `waydroid shell` / adb ✔）
- 端口默认 **27183**，监听 `0.0.0.0`（宿主可从 Windows 连 ✔）

**验收（必须贴原始输出）**
```bash
# 1) 起 Waydroid（顺序很重要，见"坑"）
systemctl restart waydroid-container
waydroid session start &
waydroid show-full-ui &          # 不加这句容器会被 lxc-freeze
# 2) 起流服务
bash tools/waydroid-stream/serve.sh --port 27183 --codec h264 --fps 30
# 3) 另一处验证（无需 T2 就绪）
ffmpeg -i tcp://10.0.0.108:27183 -frames:v 60 -f null -     # 期望：正常解码 60 帧，无错
# 或协议级自测（T3 自带一个简易客户端）
python3 tools/waydroid-stream/probe_client.py 10.0.0.108:27183 --save 30
```
- ✅ 通过 = `ffmpeg` 能解码出 ≥60 帧；`probe_client.py` 能按协议收到视频帧并把输入帧发出去、
  Android 侧有**可见反应**（点击落点/按键生效 ✔，可用 `waydroid shell -- cmd input …` 对照 ✔）

## 三、实现路线（建议顺序，任选可行者）

1. **首选：scrcpy server** —— 108 上装 `scrcpy`（`apt install scrcpy` ✔），用它的 `scrcpy-server`
   协议拿 H.264 流；把它的 socket 输出**重新打包**成 `CATCLAW/1`（写一个薄转发：Python/C 均可 ✔）。
   输入注入同样转成 scrcpy 的控制消息 ✔（或退化为 `input` 命令 ✔）。
2. **次选：`screencap` + JPEG** —— 循环 `screencap -p`（或 `waydroid shell -- screencap`）⇒ 逐帧 JPEG，
   30fps 可能达不到 ✔（能到 10~15fps 也可接受 ✔），但实现最简单 ✔、**先跑通协议** ✔。
3. **输入注入**：`waydroid shell -- input tap X Y` / `input swipe` / `input keyevent K` ✔
   （注意：`waydroid shell -- "cmd …"` **不经过 shell** ✗ ⇒ 用 `input` 这种**可执行文件**或 adb ✔）。

## 四、命令速查（108）

```bash
# 起/停
systemctl restart waydroid-container
waydroid session start & waydroid show-full-ui &
waydroid status
# 容器里执行
waydroid shell -- input tap 500 800
waydroid shell -- wm size              # 分辨率（握手要用）
# 装/查我们的 APK
waydroid app list | grep -i catclaw
# 已有的坑修复（如缺 pulse socket）
mkdir -p /run/user/0/pulse && touch /run/user/0/pulse/native
```

## 五、已知坑（都是 108 上实测过的）

1. **必须 `waydroid show-full-ui`** ✗ 否则容器被 `lxc-freeze` ✗（`session start` 不够 ✗）。
2. `session stop` 会删掉 `waydroid0` 网桥 ✗ ⇒ 再起前先 `systemctl restart waydroid-container` ✔。
3. 挂载报 `Failed to setup mount entries` ✗ ⇒ 缺 `/run/user/0/pulse/native` ✔（`touch` 即可 ✔）。
4. `waydroid prop set` 会报 `Sending reply failed` ✗ ⇒ 改 `persist.*` 请写
   `/var/lib/waydroid/waydroid_base.prop` 后重启容器 ✔。
5. `waydroid shell -- "cmd …"` **不走 shell** ✗ ⇒ 传可执行文件与参数 ✔。
6. 分辨率/方向：握手要报**真实** `wm size` ✗，否则 T2 里坐标会偏 ✗。
7. 输入坐标是**设备像素** ✗（不是窗口坐标 ✗）⇒ T2 送来的坐标需要按分辨率换算 ✔（换算放哪一侧请写进 README ✔，
   建议**客户端送设备像素** ✔，服务端原样 `input tap` ✔）。

## 六、目录建议

```
tools/waydroid-stream/
  README.md            # 用法、验证结果、协议实现说明、限制
  serve.sh             # 一键起 Waydroid + 流服务（含上面的顺序/坑修复）
  serve.py             # 协议服务端（scrcpy 转发 或 screencap-JPEG 循环）
  input.py             # 输入帧 → waydroid shell -- input …
  probe_client.py      # 自带简易客户端（收 N 帧存盘 + 发几个输入帧）
```

## 七、交付

- `tools/waydroid-stream/**` 全部新文件 + 提交（中文 Conventional Commits）✔
- README 里写清：**如何起服务、如何验证（含 ffmpeg/probe 的原始输出）、当前 fps/延迟实测值、
  与协议不一致之处（若有）** ✔
- 若为了让 T2 更好联调而想改协议 ✗：**不要直接改** ✗，在提交说明里提出 ✔（或加可选扩展字段 ✗ 并明确默认关闭 ✔）
