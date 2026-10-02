# N3｜P0 收口：单 VM + 修复真正落盘 + `screencap` 出 PNG

> 归属：**B1.1 显示栈的最后一步**（P0 验收）。
> 现状：`allocate(9) **rc=0**` 已出现 ✔（分配链路打通 ✔），但 **`screencap` 仍报 `libicu.so not found`** ✗、**无 PNG** ✗。
> 诡异点：ICU 三库（`libicu/libicuuc/libicui18n.so`）**早在 08:12 就进了打包产物** ✔
> （`JavaBridge/qemu-src/blobs/astack/android-stack.tar.gz` 内含 `system/lib64/libicu*.so` ✔）
> ⇒ 说明问题出在 **"包 → 运行时镜像 → 正在跑的那个 guest"** 这条链的**落地**上 ✗，不是打包逻辑 ✗。

## 一、已确证的三个事实（直接用 ✗）

1. **打包产物是对的** ✔：`tar tzf android-stack.tar.gz | grep libicu` ⇒ 三个库都在 ✔（包 mtime 08:12 ✗）。
2. **同时跑了两个 VM** ✗✗：
   ```
   qemu-system-x86_64  pid=44164  启动 10:20:29
   qemu-system-x86_64  pid=13044  启动 11:01:47
   ```
   ⇒ 多实例抢同一套镜像与 adb 端口 ✗ ⇒ `adb device offline` ✗、**日志证据归属不明** ✗、
   169 次开机里大量是**重复启动** ✗ ⇒ **"改了没效果"的假象有一半来自这里** ✔。
3. **`card0` vs `renderD128`** ✔：`gbm_bo_create` 在 `renderD128` 上 **EACCES** ✗、在 **`card0` 上成功** ✔
   ⇒ `gralloc.gbm.device` 必须 `/dev/dri/card0` ✔（工作树 5 处已改 ✔）。

## 二、任务（按顺序做 ✗，每步都要证据 ✗）

**【N3-0】先保证只有一个 VM（10 分钟 ✗）**
- 杀掉多余实例 ✔（保留一个 ✗），然后确认：
  ```
  Get-Process qemu-system-x86_64 | Select-Object Id,StartTime
  ```
  ⇒ **必须只有一行** ✔。此后**每次开机都先查这个** ✔（否则后面所有证据都不可信 ✗）。
- 顺手记下：应用 exe 的启动时刻 ✔ 与 QEMU 的启动时刻 ✔ 应对应 ✔。

**【N3-1】确认修复是否真的在"正在跑的 guest 里"（15 分钟 ✗）**
- 从 `%APPDATA%\CatClawVideo.debug\home-debug.log` 取最新端口 ✔：
  `adb connect 127.0.0.1:<port>` ⇒ 然后：
  ```
  adb -s 127.0.0.1:<port> shell "ls -la /system/lib64/libicu.so /system/lib64/libicuuc.so /system/lib64/libicui18n.so"
  adb -s 127.0.0.1:<port> shell "/system/bin/gbmprobe 2>&1 | tail -6"
  adb -s 127.0.0.1:<port> shell "screencap -p > /data/local/tmp/s.png; head -c 8 /data/local/tmp/s.png | od -An -tx1"
  ```
- 判读 ✗：
  - ICU **不在** ✗ ⇒ 镜像陈旧 ✔ ⇒ 走 N3-2 ✔；
  - ICU **在** ✗ 但 `screencap` 仍 `CANNOT LINK` ✗ ⇒ 是 **linker namespace / `ld.config`** 问题 ✔
    （`libharfbuzz_ng.so` 需 `libicu.so` ✗，但 namespace 搜索路径没含 `/system/lib64` ✗ 或
    该文件属 APEX 命名空间 ✗）⇒ 记录 `ld.config.txt` 里 `default` 命名空间的 `search.paths` ✔。

**【N3-2】干净重建一次并留证（30 分钟 ✗）**
```powershell
cmd /c tools\b1-build\rebuild_gb.cmd
```
- 注意 `rebuild_gb.cmd` 的**依赖顺序**（脚本头写明 ✗）：`build_bootimg_inject.py` → `inject_dex.py`
  → `check_image.py`（**check 失败绝不要部署** ✗）→ deploy ✔。
- ⚠ 打包器在 **108** 上跑 ✗ ⇒ 跑完**必须把产物取回** ✗（`android-stack.tar.gz` / `mesa-gl.tar.gz`），
  否则本地重建用的是旧 tar ✗（这是历史反复踩的坑 ✔）。
- 留证 ✗：贴出 tar 的时间戳 ✔ + 重建日志尾部 ✔ + 之后 guest 内 `ls -la /system/lib64/libicu*` ✔。

**【N3-3】验收（10 分钟 ✗）**
- `<最后一次 QEMU 启动>` 段内应同时出现 ✗：
  - `<< allocate(9) rc=0` ✔
  - **PNG 魔数 `89504E47`** ✔（`/system/bin/screencap` 直调成功 ✗）
- 拿到 PNG ⇒ **P0 收口** ✔，把图存进 `docs/research/framework/evidence/` ✔。

**【N3-4】收尾：剩余两次分配失败（可选 ✗，半天 ✗）**
- 本次开机 4 次分配结果是 `rc=0 / rc=0 / **rc=7** / **rc=5**` ✗ ⇒ 后两个是谁发的、为什么失败 ✗
  （`rc=5` = mapper `NO_RESOURCES` ✗；`rc=7` 不在该枚举里 ✗，先确认它出自哪个错误空间 ✔）。
- 做法：在 allocate 垫片里**失败时打回溯 + 参数** ✗（`PROPFIX_ALLOC=1` ✔ 已有关闭开关 ✗），
  对照 `w/h/fmt/usage` ✗ 与调用者 ✗。

## 三、判据

| 级别 | 判据 |
|---|---|
| 最小 | `Get-Process qemu-system-x86_64` **只有一行** ✔ + guest 内能看到 ICU 三库 ✔ |
| 中间 | `screencap -p` 输出以 `89 50 4E 47` 开头 ✔ |
| 收口 | 最后一次开机段内同时有 `rc=0` ✔ 与 PNG 魔数 ✔，且证据图入库 ✔ |

## 四、边界与纪律

- **写范围**：`JavaBridge/qemu-src/**` ✔、`tools/b1-build/**` ✔、`docs/**` ✔。
  不要碰 `tools/stream-probe/**`（T2 ✗）、`tools/waydroid-stream/**`（T3 ✗）、`CatClawVideo.Maui/**`（N4 ✗）。
- ⚠ **一次只跑一个 VM** ✔；重建前先 `git status` 看有没有别人的在途改动 ✗（当前横跨 4 条轨道 ✗）。
- 取证纪律 ✗：**永远先定位最后一次 `===== QEMU 启动`** ✔，只在其后筛选 ✔
  （日志已 471MB/169 次开机 ✗，否则结论必被污染 ✔）。
- 提交：中文 Conventional Commits，多行 `git commit -F 文件` ✔。

## 五、踩坑提醒

1. `adb device offline` ✗ 十有八九是**多 VM** ✗ 或 VM 正在重启 ✗ —— 先查进程表 ✗，别怀疑镜像 ✔。
2. `dri 目录` 自检回声只显示 `iris_dri.so` ✗ **不代表软链失败** ✔（打包器已用 tarfile 直写 ✔，
   且 `gbm_create_device` 已返回有效指针 ✔）⇒ 别照它排查 ✔。
3. `PROPFIX_LOGV` 默认必须关 ✗（开了会崩 SF ✗）；`PROPFIX_ALLOC` 默认关 ✔，要查分配再开 ✔。
4. 打包器在 108 上跑 ✗ ⇒ **产物必须取回** ✔。
