# N1｜E4 冲刺：让 zygote / system_server 活下来，`pm install` 能装能起

> 归属：**B1.2 总目标的终判**（`init.svc.zygote=running` + `sys.boot_completed=1` + `pm install` 成功）。
> 前置：E1 ✅ / E2 ✅（shadow PID1，`init.svc` 0→48/54）/ E3 ✅（apexd 销案）都已完成 ✔。
> 当前唯一卡点：**zygote 走到 ART 主线程后 `SIGABRT`** ✗。
> 与 `8dd9fd4`（b1.2/E4推进）是**同一目标** ⇒ 若该线仍在进行，本包作为**加强包**（补取证 + 逐字环境 diff），
> **不要并行重建镜像** ✗（会互相覆盖 `blobs/e2/**` 与真 init 编排 ✗）。

## 一、已知事实与证据（可直接引用，别重查 ✗）

1. **zygote 已推进到 ART 主线程** ✗：
   - 补了 `ANDROID_DATA`（原文：`ANDROID_DATA environment variable unset` ✔）
   - 补了 `BOOTCLASSPATH`/`SYSTEMSERVERCLASSPATH`：**从 108 活着的 surfaceflinger 的 `/proc/PID/environ` 取回**
     权威值，落成 `blobs/e2/android-env.sh`（1624B / 463B ✔），由分发器 `exec` 前 source ✔。
   - 结果：zygote **退出码 134**、`Fatal signal 6 (SI_QUEUE)` ✗（= `abort()` ✗）。
2. **崩溃现场取证很难**：`tombstoned` **21.7s 才起来** ✗ ⇒ 前几次崩溃**没有 crash 处理者** ✗；
   `lmkd` 被标 critical ⇒ **35.5s 整机重启** ✗。
3. **32 位那条线是死路** ✗：`app_process32` 缺 `libandroid_runtime.so`（无 32 位运行时 ✗）
   ⇒ **不要在 32 位上花时间** ✗（把资源集中在 64 位 zygote ✗）。
4. `--zygote-log` 已存在 ✔：用 `/zygwrap.sh` 包 zygote/zygote_secondary ✗，把服务 stderr 落文件 + 保真实退出码 ✔
   （注意：`"$@" | while read` 会把退出码洗成 0 ✗，第 ⑳ 次开机就被骗过一轮 ✗）。

## 二、任务（按顺序做 ✗）

**【N1-1】先拿到 ART 的 abort 原文（半天 ✗）**
- `zygwrap.sh` 抓到的 zygote stderr 文件里应有 ART 的 `FATAL`/`Aborting`/`CHECK failed` 行 ✗
  ⇒ **把首次崩溃前的最后 40 行原文贴出来** ✔（这是本任务唯一必须产出的证据 ✔）。
- 若 stderr 里没有 ✗ ⇒ 用 `debuggerd -b <pid>` 或先把 `tombstoned` 提前拉起 ✗（改 init 编排 ✗），
  再复现一次 ✔；也可以让 ART 打更多日志：`setprop dalvik.vm.extra-opts -verbose:...` ✗。

**【N1-2】逐字 diff 108 的 zygote 环境（半天 ✗）**
- 参照系取 **zygote 自己的**，不是 SF ✗：
  ```
  ssh root@10.0.0.108 'PID=$(pidof zygote64 || pidof zygote); tr "\0" "\n" < /proc/$PID/environ | sort'
  ```
  同法取 `cmdline`、`/proc/$PID/maps` 里 `/apex/com.android.art/**` 的加载列表 ✗。
- 与 guest 内 zygote 的**同一份输出**逐行 diff ✔ ⇒ 缺哪个变量/哪个 APEX 库就是答案 ✔。

**【N1-3】重点怀疑：ART 需要的属性我们没给（半天 ✗）**
- 我们的属性来源是**编译期假属性表** ✗（`PROPFIX` 白名单 ✗）⇒ ART 读 `dalvik.vm.*` 时可能拿到 NULL ✗
  ⇒ ART 有多处 `LOG(FATAL)` 会直接 abort ✗。
- 做法：对 108 的 zygote 跑 `strings /proc/$PID/environ` ✗ + 看它启动时读过的 `dalvik.vm.*` ✗
  （可用 `setprop` 全量对比 ✗：`getprop | grep dalvik` 两边 diff ✗）⇒ **把缺的补进假属性表** ✔。
- 注意 E2 结论 ✗：**「编译期假属性表」与「真属性区」互斥** ✗ ⇒ 在 shadow PID1 形态下属性区归真 init ✗，
  所以要么走真属性区补 prop ✗，要么在分发给 zygote 前 `export` ✗（与 `android-env.sh` 同法 ✔）。

**【N1-4】通过后再推 E4 终判（1 天 ✗）**
- `pm install` 我们自己的 debug APK ⇒ `am start` ✔
- 若 `init.svc.zygote=running` 但 app 起不来 ✗ ⇒ 战场转到 **VINTF / privapp 权限 / servicemanager 注册** ✗，
  按 T4 的差距矩阵继续 ✔。

## 三、判据（可验证 ✗）

| 级别 | 判据 |
|---|---|
| 最小 | 拿到 ART abort **原文**（`FATAL` 行 ✗）并写进文档 ✔ |
| 中间 | `getprop init.svc.zygote` = **running**（不再是 `restarting` ✗）且不再被 `signal 9` 反复杀 ✔ |
| 终判 | `sys.boot_completed=1` ✔ + `pm install` 装得上 ✔ + `am start` 起得来 ✔ |

## 四、边界与纪律

- **写范围**：`JavaBridge/qemu-src/**` ✔、`tools/b1-build/**` ✔、`docs/research/framework/**` ✔、`docs/tasks/**` ✔
  ⇒ **不要碰** `tools/stream-probe/**`（T2 ✗）、`tools/waydroid-stream/**`（T3 ✗）、
  `CatClawVideo.Maui/**`、`CatClawVideo.Core/**`（宿主窗口 ✗）。
- **必须可回退**：每次开机后跑 `--off` 回退并核对 md5 ✔（现基线 `75702977759e41d985b6385969f2876d` ✔、桥 `:1` ✔）。
- **每次重建前先 `git status`** ✔：本仓当前未提交改动横跨 4 条轨道 ✗ ⇒ 混构建会让你白跑 ✔。
- 提交：中文 Conventional Commits，多行用 `git commit -F 文件` ✔（PowerShell 直接传参会被吃掉 ✗）。

## 五、踩坑提醒（血泪 ✔）

1. **只看最后一次开机** ✔：`qemu-console-art.log` 已 471MB/几十次开机 ✗ ⇒ 先定位最后一条
   `===== QEMU 启动`，再在**其后**筛选 ✗，否则结论被旧开机污染 ✔。
2. ART 的 abort 在 **stderr** ✗，不在 logd ✗（我们属性区/日志链路不完整 ✗）。
3. `dalvik.vm.*` 这类属性在**假属性表缺失时返回 NULL** ✗，ART 会把它当致命错误 ✗。
4. 别用 `"$@" | while read` 包服务 ✗（退出码被洗成 0 ✗）。
