# tools/triage —— B1 单次开机取证（只读，零副作用）

## 为什么有它

2026-10-02 一天的教训（都是真事 ✗）：

1. `qemu-console-art.log` 涨到 **580MB+、开机标记 173 个** ✗ ⇒ 直接取"最后一条匹配"极易命中**旧开机**，
   于是得出完全错误的结论 ✗（当天发生过多次）。
2. 其中**绝大多数"开机"其实没启动成功**（存活 0 秒、guest 零输出 ✗）⇒ 把"开机号"当进度是错的 ✗。
3. guest 会以**两种形态**启动 ✗：`real-init`（`init.svc` 有值 ✔）与 `claw-legacy`（无 ✗）
   ⇒ **跨形态比较结论 = 自欺欺人** ✗（唯一出现过 `allocate rc=0` 的是 real-init 那次 ✗）。
4. 输出必须是 **ASCII** ✗：往 GBK 控制台打印 `✔/✗/⇒` 会 `UnicodeEncodeError`（踩过 ✗）。

## 用法

```bash
# 单次开机判定（默认最后一次；退出码 0 = P0 闸门全过）
python tools/triage/boot_triage.py

# 指定倒数第 N 次开机
python tools/triage/boot_triage.py --boot 2

# 跨开机矩阵（回归闸门：一眼看出哪次通、哪次退）
python tools/triage/boot_triage.py --matrix 8

# 开机时间线
python tools/triage/boot_triage.py --all-boots --boots 8

# 机器可读
python tools/triage/boot_triage.py --json
```

`--log` 可指定别的日志路径（默认取 `%LOCALAPPDATA%\CatClawVideo.debug\qemu-console-art.log` ✔）。

## 判读

| 列 / 计数 | 含义 |
|---|---|
| `mode` | `real-init` = 该次开机有 `init.svc.*`（shadow/真 init ✗）；`claw-legacy` = 没有 ✗ |
| `alive` | 该次开机内**最后一条内核时间戳**（≈ 存活秒数 ✔）；`0s` = guest **根本没输出** ✗ |
| `SFstart` / `sf-lines` | `SurfaceFlinger is starting` 次数 / `[sf]` 行数 ⇒ SF 是否真的跑起来 ✔ |
| `alloc=0` | `<< allocate(9) rc=0` 次数 ⇒ 缓冲分配是否成功 ✔ |
| `PNG` | PNG 魔数 `89504E47` ⇒ `screencap` 是否出图 ✔（**P0 终判** ✗） |
| `init.svc` | `init.svc.*` 行数 ⇒ 属性区/真 init 是否健康 ✔ |
| `AIDLspam` | `SurfaceFlingerAIDL could not be found` 次数 ⇒ SF 没注册 AIDL ⇒ `screencap` 必挂 ✗ |
| `areaERR` | `area_init` 次数 + `EBADF` ⇒ 属性区坏了 ⇒ `getprop` 空 ✗ |
| `zyg-kill` | init `Sending signal 9 to service 'zygote'` ⇒ zygote 被反复杀 ✗（E4 卡点 ✗）|
| `crash` | 我们 shim 的崩溃处理器打印（`收到信号` ✗）|

## 退出码

- `0`：5 项闸门全过（分配 ✔ + SF 有输出 ✔ + SF starting ✔ + PNG ✔ + `init.svc` 有值 ✔）
- `1`：有闸门未过（**当前常态** ✗）
- `2`：用法或日志文件错误

## 纪律（血泪 ✗）

1. **永远只看一次开机** ✔ —— 这个工具就是干这个的 ✗。
2. **不要跨形态比较** ✗（`real-init` 与 `claw-legacy` 是两种不同的 guest ✗）。
3. 结论之前先确认**进程唯一性** ✔：只能有一个 `CatClawVideo.Maui` ✔ 和一个 `qemu-system-x86_64` ✔
   （重复实例会连到**另一台** guest ✗ —— 当天因此产生过假结论 ✗）。
4. 这个工具**只读** ✔：不改仓库、不动镜像、不碰 VM ✔，可以随时跑 ✔。
