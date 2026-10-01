# T4 探针｜框架层路线预研（只读）

五个探针 + 一支对照脚本，全部**只读**：不写镜像、不进 `JavaBridge/qemu-src/**`、不改 108 的配置。
同一套判据既能跑我们的 guest initrd，也能跑 108 上挂载的 Waydroid `system.img` —— 换输入不换判据才叫对照。

## 用法

```bash
# 1) 审计我们的 guest（默认打部署件 CatClawVideo.Maui/QemuGuest/x86guest/art_initrd_x64.gz）
python tools/framework-probe/audit_apex.py
python tools/framework-probe/audit_rc.py
python tools/framework-probe/audit_props.py
python tools/framework-probe/audit_selinux.py
python tools/framework-probe/audit_apk_needs.py

# 1b) guest **运行时**只读盘点（需要应用在跑、guest 已起；探针自己从 home-debug.log 找 adb 端口）
sh tools/framework-probe/probe_guest_runtime.sh

# 2) 对照：108（有完整框架在跑的那台）
ssh root@10.0.0.108 'rm -rf /tmp/t4probe'
scp -r tools/framework-probe root@10.0.0.108:/tmp/t4probe
ssh root@10.0.0.108 'sed -i "s/\r$//" /tmp/t4probe/*.sh /tmp/t4probe/*.py; sh /tmp/t4probe/remote_probe.sh'

# 3) 只对我们自己的镜像跑（脚本化，输出直接进证据目录）
for s in apex rc props selinux; do python tools/framework-probe/audit_$s.py \
     > docs/research/framework/evidence/guest-$s.txt 2>&1; done
```

零第三方依赖（只用标准库）。Python ≥3.8。

## 退出码与运行环境（2026-10-02 审核后的定稿）

- **退出码语义**：`0` = 正常跑完（**不代表"没有差距"** —— 差距是输出里的内容，不是退出码）；
  `1` = 未捕获异常（会带 Traceback）；`2` = 输入不可用（如 `audit_apk_needs.py` 找不到 APK）。
- **不再需要 `PYTHONIOENCODING=utf-8`**：`cpioimg.py` 与 `audit_apk_needs.py` 开头已把
  stdout/stderr 重设成 UTF-8。修之前的实测正控制：HEAD 版在 GBK 控制台下于
  `cpioimg.py:172 table()` 抛 `UnicodeEncodeError: 'gbk' codec can't encode character '\u2714'`，
  **exit=1、证据文件只写下 407 B（完整版 2,211 B ⇒ 截掉 82%）** —— 审核说的"证据在首个特殊
  字符处截断"成立。
- ⚠ **别用 `env`/`env -u VAR` 启动这些脚本**：Git Bash 的 `env` 起 Windows 原生 python 时
  会丢继承来的 stdout 句柄，症状是 **exit=0、输出 0 字节**（看着像"探针修坏了"，其实是启动器）。
  要清环境变量就用 `PYTHONIOENCODING= python …` 或直接依赖上面那条 UTF-8 重设。
- 判"跑好了"的最低标准：`exit=0` **且** 输出字节数与预期同量级 **且** `grep -c Traceback` 为 0。
  只看退出码会同时放过"0 字节的 env 坑"和"截断的编码坑"。

## 结论摘要（数字都出自本目录 `evidence/`，可复现）

| 事实 | 我们的 guest | 108 参照系（框架完整在跑） |
|---|---|---|
| 必需 APEX 内容 | 6 个**已解包**在 `/apex`（98 MiB），`.apex` 容器 0 个 | `system/apex` 下 25 个解包目录；运行时 25 个 overlay 挂载点、`ro.apex.updatable` **未设** |
| init rc | 69 文件 / 3557 行 / 79 service / 212 `on` / 107 `setprop` / 1007 mkdir-chown | 74 / 3586 / 82 / 216 / 107 / 1011 |
| service 引用的可执行缺失 | **4 个**，全是 `/vendor/bin/hw/*` | 11 个（同样在 vendor 侧，vendor 是另一个 img） |
| 属性：编译期表 vs rc 要写 | 表 161 条（98 ro / 63 非 ro）；rc 启动路径写 **47 键（46 个非 ro）** | build.prop 131 条（80/51）；同样 47 键要写 |
| 属性区 `/dev/__properties__` | 镜像里**没有**，也没人建 | **268 项**：`properties_serial` 131072B + `property_info` 74676B + 每个 SELinux context 一个 131072B 文件 |
| SELinux | `plat_sepolicy.cil` 2049 KiB 在、`precompiled_sepolicy` 缺、`secilc` 在 | 同一份 cil；`precompiled_sepolicy` 在 **vendor.img** 里；**`getenforce = Disabled`** |
| 运行时 | 无 `init.svc.*`、无 zygote | `init.svc.*` 145 条 / 52 running、`zygote=running`、`apexd.status=ready`、`sys.boot_completed=1` |
| TVBox APK 的 framework 面 | — | 引用 android/dalvik 类 1908 个，framework 包 802 个；我们的桩覆盖 103 个 ⇒ 缺 **699** |

三条最要紧的判读：

1. **SELinux 不是框架启动的硬前置** —— 参照系以 `Disabled` 跑着完整 framework（zygote running）。
   所以"必须先补 SELinux 策略"这一大块工作量可以从路线图上划掉（但见下面第 3 点的属性区细节）。
2. **属性服务必须可写** —— rc 在启动路径上要写 46 个非 `ro.` 键，framework 靠 `on property:` 推进
   （`sys.boot_completed` 被读 21 次）。per-process 编译期表在架构上不可能满足：既跨进程不可见，也不能写。
3. **属性区的真身不是 kernel policy，而是 init 建的一堆文件** —— 参照系 268 项里文件名就是
   `u:object_r:aac_drc_prop:s0` 这种 context，内容来自 `plat_property_contexts`（我们镜像里已有 85 KiB）。
   ⇒ B1 文档第 16 条"`area_init=-1` 疑因缺 SELinux 策略"必须用一次实测复核，别当定案。

## 文件

- `cpioimg.py` —— 只读镜像/目录扫描器（newc cpio 流式解析 + 目录模式）。
  ⚠ cpio 的 4 字节对齐要按**绝对偏移**算，按条目内相对偏移算会"只解析出 1 个条目且不报错"（写的时候就踩过）。
- `audit_apex.py` / `audit_rc.py` / `audit_props.py` / `audit_selinux.py` / `audit_apk_needs.py`
- `remote_probe.sh` —— 108 对照（`mount -o ro,loop` + 容器内问句）。
  ⚠ 容器内问句必须 `waydroid shell -- sh -c '…'`；少了 `-- sh -c`，管道与 `$(…)` 会被 waydroid 的
  argparse 吃掉，症状是 `unrecognized arguments` 或整段返回空（实测踩过）。
  ⚠ `scp -r` 到**已存在**的目录会变成"目录套目录"，远端跑的还是旧脚本 ⇒ 先 `rm -rf`。

证据落盘在 `../../docs/research/framework/evidence/`。
