#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""E2｜shadow PID1 —— 一条命令开、一条命令关的镜像改造（不动宿主 C#、不动 T1 的文件）。

思路（为什么不需要改 `-append`）：内核 cmdline 已经是 `rdinit=/init`，而我们的 `/init` 就是 PID1。
所以只要让它在带标记时 `exec /system/bin/init`，真 init 就**顶替同一个 PID1** 接手 ——
和正常 initramfs→init 的交接一模一样，宿主侧一行都不用动。

真 init 第二阶段会自动 import `/system/etc/init/*.rc`，所以原编排（挂载/网络/HAL/artlaunch 桥）
整体留在 `/init.claw` 里，由新增的 `/system/etc/init/init.claw.rc` 作为服务拉回来。

判据（E2 的成败都从串口读，不依赖 adb —— 当前 adbd 起不来，见报告 §E1 末尾）：
  `/e2probe.sh` 在 `sys.boot_completed=1` 时把
  `init.svc.*` 条数、`/dev/__properties__` 项数、zygote 状态写进 `/dev/kmsg`（前缀 `<4>` 强制过 loglevel=4），
  串口里就能看到 `[E2] ...` 那行。

用法：
  python tools/b1-build/shadow_init.py            # 开：部署件 → .zwork/art_initrd_x64_shadow.gz 并就地部署
  python tools/b1-build/shadow_init.py --off      # 关：把备份件放回去（一键回退）
  python tools/b1-build/shadow_init.py --dry      # 只生成注入件，不动镜像
⚠ 部署前必须**先关应用**（镜像被 QEMU user-mapped 占用，复制会失败）。
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "framework-probe"))
import cpioimg as C                                            # noqa: E402

Z = os.path.join(REPO, "CatClawVideo.Maui", "QemuGuest", "x86guest")
SRC = os.path.join(Z, "art_initrd_x64.gz")
RUN = os.path.join(REPO, "CatClawVideo.Maui", "bin", "Debug", "net11.0-windows10.0.26100.0",
                   "win-x64", "QemuGuest", "x86guest", "art_initrd_x64.gz")
WORK = os.path.join(REPO, ".zwork")
OUT = os.path.join(WORK, "art_initrd_x64_shadow.gz")
BAK = os.path.join(WORK, "art_initrd_x64.pre-shadow.gz")
def find_tool(preferred, *candidates):
    for p in (preferred,) + candidates:
        if os.path.exists(p):
            return p
    print("!! 找不到工具：%s" % preferred)
    return None


INJECT = find_tool(os.path.join(HERE, "inject_dex.py"),
                   os.path.join(REPO, ".zwork", "inject_dex.py"),
                   os.path.join(Z, "inject_dex.py"))
CHECK = os.path.join(REPO, "JavaBridge", "qemu-src", "tools", "x86guest", "check_image.py")
MARK = "# shadow-PID1 分发器"
WANT_IN_IMAGE = {"init", "init.claw", "init.rc", "system/etc/init/init.claw.rc",
                 "e2probe.sh", "shadow-init", "system/build.prop", "e2-vendor-build.prop"}
# 108 参照系（Waydroid x86_64 / Android 13）里逐字取回的两份 build.prop：
#   ssh root@10.0.0.108 'mount -o ro,loop /var/lib/waydroid/images/system.img /tmp/e2pk; \
#     mount -o ro,loop /root/x86guest/vendor-ex/vendor.img /tmp/e2pv; \
#     cp /tmp/e2pk/system/build.prop /tmp/e2out/system.build.prop; \
#     cp /tmp/e2pv/build.prop /tmp/e2out/vendor.build.prop'
BLOBS = os.path.join(REPO, "JavaBridge", "qemu-src", "blobs", "e2")

DISPATCH = """#!/bin/busybox sh
%s
# 有 /shadow-init 标记 ⇒ 真 Android init 顶替同一个 PID1 接手（E2 实验）；
# 没标记 ⇒ 原样跑我们自己的编排。两条路都不需要改宿主 -append。
BB=/bin/busybox
$BB mkdir -p /proc /sys /dev /tmp
$BB mount -t proc proc /proc 2>/dev/null
$BB mount -t sysfs sys /sys 2>/dev/null
$BB mount -t devtmpfs devtmpfs /dev 2>/dev/null
# 宿主 cmdline 是 loglevel=4 ⇒ 优先级 >3 的 init 日志被串口丢掉（实测第三次开机：
# 5.83s 还有 warning，5.85s 直接 reboot，中间一行没见着）。把 console_loglevel 抬到 7 才看得清。
echo 7 4 1 7 > /proc/sys/kernel/printk 2>/dev/null
# 实测（06:35）：init 死前有 "printk: init: 28 output lines suppressed due to ratelimiting"
# ⇒ 真正的 FATAL 原因被 /dev/kmsg 的**按进程限速**丢掉了。关掉 devkmsg 限速才看得见。
echo on > /proc/sys/kernel/printk_devkmsg 2>/dev/null
echo 0 > /proc/sys/kernel/printk_ratelimit 2>/dev/null
# 实测：第 2 次开机只有 init.claw 的输出、没有 shadow 行 ⇒ 分不清"标记没了"还是"走了服务路径"，
# 所以开机第一件事就把标记状态写到串口。
echo "[init] 分发器在位 shadow=$([ -e /shadow-init ] && echo Y || echo N) initd=$([ -x /system/bin/init ] && echo Y || echo N)"
if [ -e /shadow-init ]; then
    # 真 init 的早期步骤会 mount tmpfs 到这几个点 —— 挂载点目录必须先存在，
    # 否则 mount 返 ENOENT，first stage 直接判定"有错误"⇒ InitFatalReboot（2026-10-02 实测）。
    for _d in /mnt /mnt/vendor /mnt/product /debug_ramdisk /second_stage_resources \\
              /first_stage_ramdisk /metadata /bootstrap; do
        $BB mkdir -p $_d
    done
    echo "[init] shadow-PID1：exec /system/bin/init %s"
    # 第二块石头（实测 2026-10-02 06:12）：second stage 的 SelinuxGetVendorAndroidVersion()
    # 要读 /vendor/etc/selinux/plat_sepolicy_vers.txt；我们的 initrd 里没有 /vendor，
    # 读不到就 LOG(FATAL) ⇒ InitFatalReboot。108 参照系该文件在 vendor.img 里，5 字节 "33.0\\n"，逐字补。
    $BB mkdir -p /vendor/etc/selinux
    printf '33.0\\n' > /vendor/etc/selinux/plat_sepolicy_vers.txt
    # 石头③（实测 06:20）：整张 initrd 里**一个 .prop 都没有**（审计：build.prop 命中 0 条），
    # 真 init 的属性区建得起来但里面是空的 ⇒ 把 108 参照系的两份 build.prop 逐字补进来。
    # /vendor 在 initrd 里根本不存在（vendor 内容由运行时的 astack 包解出），所以这条走 cp。
    cp /e2-vendor-build.prop /vendor/build.prop 2>/dev/null
    echo "[init] build.prop: system=$($BB wc -c < /system/build.prop 2>/dev/null)B vendor=$($BB wc -c < /vendor/build.prop 2>/dev/null)B"
    # 石头④（实测 06:40）：init 报 "start_property_service socket creation failed:
    # Failed to bind socket 'property_service': ENOENT" ⇒ 它要在 /dev/socket 上 bind，
    # 而这个目录本来由 **first stage** 建；我们用 second_stage 直接进第二阶段就没人建它。
    # /dev 是 devtmpfs（devtmpfs 允许 mkdir），另外顺手把 first stage 会做的 devpts 也补上。
    $BB mkdir -p /dev/socket /dev/pts
    $BB mount -t devpts devpts /dev/pts 2>/dev/null
    echo "[init] /dev/socket=$($BB ls -d /dev/socket 2>/dev/null) pts=$($BB mount 2>/dev/null | $BB grep -c devpts)"
    # 石头⑥（实测 06:54）：init 起了 34 个服务，**26 个 cannot execv … ENOENT**，
    # 连 app_process64 / hwservicemanager / logd 都在内。文件明明在镜像里，
    # 但它们的 PT_INTERP=/system/bin/linker64，而那条目是**指向 /apex/com.android.runtime/bin/linker64 的软链**
    # —— init 的 SetupMountNamespaces() 对 /apex 做了 bind/私有挂载，解释器在新 ns 里就找不到了。
    # 解法：把 apex 里的真 linker 落成 /system/bin 下的实体文件（1.7MB 本来就在镜像里，不加体积）。
    for _l in linker64 linker; do
        _a=/apex/com.android.runtime/bin/$_l
        if $BB test -L /system/bin/$_l && $BB test -f $_a; then
            $BB rm -f /system/bin/$_l
            $BB cp $_a /system/bin/$_l
            $BB chmod 755 /system/bin/$_l
            echo "[init] $_l: 软链→实体 $($BB wc -c < /system/bin/$_l 2>/dev/null)B"
        else
            echo "[init] $_l: 未替换 islink=$($BB test -L /system/bin/$_l && echo Y || echo N) apex=$($BB test -f $_a && echo Y || echo N)"
        fi
    done
    [ -f /modules/binder_linux.ko ] && $BB insmod /modules/binder_linux.ko devices=binder,hwbinder,vndbinder 2>&1
__CLAWBG__
__E2RUN__
fi
exec $BB sh /init.claw
""" % (MARK, "__E2ARGS__")

# 两种接手方式：
#   exec  —— 真 init 顶替同一个 PID1（E2 的正式形态）；
#   child —— 真 init 作为 PID1 的子进程跑，死了由我们上报退出码并继续探测。
#             用途：exec 形态下 init 在 6.18s **一行错误都没留**就 S5 重启（实测 06:25），
#             没有退出码就无法定因；child 形态把"静默自杀"变成可判读的信号。
RUN_EXEC = "    exec /system/bin/init %s"
# rc 触发器形态实测不成立（07:31）：/system/etc/init/init.claw.rc 里的 service 定义**能注册**
# （重复定义会被 init 点名），但挂在 on boot / on early-boot / on post-fs-data 上的
# `exec`/`start` 一次都没执行 ⇒ 换成"PID1 先后台挂编排 + 探测，再 exec 真 init"：
# exec 只换镜像不换进程表，后台子进程由真 init 收养后继续活。
CLAW_BG = """    $BB sh /e2probe.sh > /dev/kmsg 2>&1 &
    $BB sh /init.claw > /dev/ttyS0 2>&1 &
    $BB sleep 2
    echo "<3>[E2] 后台编排已挂（claw + probe），现在 exec 真 init" > /dev/kmsg"""
RUN_CHILD = """    /system/bin/init %s > /tmp/init.err 2>&1 &
    IP=$!
    echo "<3>[E2] init 以子进程启动 pid=$IP（child 诊断形态）" > /dev/kmsg
    while $BB kill -0 $IP 2>/dev/null; do
        $BB sh /e2probe.sh once
        $BB sleep 5
    done
    wait $IP
    echo "<3>[E2] init 退出 rc=$?（6/134=SIGABRT 139=SIGSEGV 132=SIGILL 0=正常返回）" > /dev/kmsg
    echo "<3>[E2] init stderr 前 40 行:" > /dev/kmsg
    $BB head -40 /tmp/init.err > /dev/kmsg 2>/dev/null
    $BB sh /e2probe.sh once
    while true; do $BB sleep 3600; done"""

CLAW_RC = """# E2：把原编排（网络/HAL/artlaunch 桥）作为 init 的服务拉回来。
# 实测（06:45 / 07:19）：`Parsing file /system/etc/init/init.claw.rc` 有、无解析错，
# 但 `write /dev/kmsg` 写的哨兵一次都没出现，`starting service 'e2probe'` 也没有
# ⇒ 分不清"action 没跑"还是"write/start 各自没生效"。改用 init 的 `exec`：
#   它一定会打 "starting service 'exec N (/bin/busybox sh /e2probe.sh)'"，
#   并且顺手把一条 [E2] 判据写进 kmsg ⇒ 一条命令同时判"action 跑没跑"和属性区读数。
service clawboot /bin/busybox sh /init.claw
    class core
    user root
    group root
    disabled
service e2probe /bin/busybox sh /e2probe.sh
    class core
    user root
    group root
    disabled
    oneshot
on property:ro.build.version.sdk=*
    exec -- /bin/busybox sh /e2probe.sh once
    start e2probe
on post-fs-data
    exec -- /bin/busybox sh /e2probe.sh once
    start e2probe
    start clawboot
on early-boot
    exec -- /bin/busybox sh /e2probe.sh once
    start e2probe
    start clawboot
on boot
    exec -- /bin/busybox sh /e2probe.sh once
    start clawboot
on property:sys.boot_completed=1
    start clawboot
    start e2probe
on property:hwservicemanager.ready=true
    start clawboot
"""

E2PROBE = """#!/bin/busybox sh
# E2 判据：不依赖 adb（当前 adbd 起不来），直接把结论写进 /dev/kmsg。
# 前缀 <4> 是 KERN_WARNING；分发器已把 console_loglevel 抬到 7，两级都看得见。
BB=/bin/busybox
# 实测：PID1 的 PATH 里没有 /system/bin ⇒ 裸 getprop 找不到，probe 会误报"读不到属性"。
PATH=/system/bin:/bin:/bin/busybox
export PATH
line() {
    T=$($BB cut -d. -f1 /proc/uptime 2>/dev/null)
    SVC=$(getprop 2>/dev/null | $BB grep -c "init.svc")
    GP=$(getprop 2>/dev/null | $BB wc -l)
    PROPS=$(ls /dev/__properties__ 2>/dev/null | $BB wc -l)
    echo "<4>[E2] t=${T}s init.svc=${SVC:-?} props=${PROPS:-?} getprop可读=${GP:-?} zygote=$(getprop init.svc.zygote 2>/dev/null) boot=$(getprop sys.boot_completed 2>/dev/null) sdk=$(getprop ro.build.version.sdk 2>/dev/null) hw=$(getprop ro.hardware 2>/dev/null) svcmgr=$(getprop servicemanager.ready 2>/dev/null)" > /dev/kmsg
}
if [ "$1" = once ]; then line; exit 0; fi
i=0
while [ $i -lt 24 ]; do
    line
    $BB sleep 5
    i=$((i+1))
done
echo "<4>[E2] probe 结束" > /dev/kmsg
"""


def extract(image, names):
    want = lambda n: n in names                                    # noqa: E731
    ents, fp = C.load(image, want=want)
    got = {}
    for e in ents:
        if e.name in names and e.data is not None:
            got[e.name] = e.text
    return got, fp


def deploy(new_image):
    for dst in (SRC, RUN):
        tmp = dst + ".new"
        shutil.copyfile(new_image, tmp)
        os.replace(tmp, dst)
    print("  已部署到仓库位与运行位")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--off", action="store_true", help="回退：把 pre-shadow 备份放回原位")
    ap.add_argument("--dry", action="store_true", help="只生成注入件，不动镜像")
    ap.add_argument("--init-args", default="second_stage",
                    help="传给 /system/bin/init 的参数（空串=裸 first stage）")
    ap.add_argument("--child", action="store_true",
                    help="诊断形态：真 init 作为 PID1 的子进程跑，死了上报退出码（默认 exec 顶替 PID1）")
    ap.add_argument("--strip-seclabel", action="store_true",
                    help="摘掉所有 rc 服务的 seclabel（内核 SELinux 开着但没策略 ⇒ setexeccon 一律 EACCES）")
    ap.add_argument("--claw-bg", action="store_true",
                    help="exec 真 init 之前先在后台挂上原编排 + 探测器（rc 触发器实测不生效，用它替代）")
    a = ap.parse_args()

    if a.off:
        if not os.path.exists(BAK):
            print("!! 没有备份件 %s，无法回退" % BAK)
            return 1
        deploy(BAK)
        print("已回退到 shadow 之前的镜像（E2 关闭）")
        return 0

    os.makedirs(WORK, exist_ok=True)
    gen = os.path.join(WORK, "shadow_files")
    os.makedirs(gen, exist_ok=True)

    cur, fp = extract(SRC, {"init", "init.claw", "system/etc/init/hw/init.rc"})
    print("源镜像:", fp)
    if "init" not in cur:
        print("!! 镜像里没有 /init")
        return 1
    # init.claw：已经改造过就用现成的，否则把当前 /init 原样存一份
    claw = cur.get("init.claw")
    if MARK in cur["init"] and not claw:
        print("!! /init 已是分发器但没有 /init.claw —— 状态不一致，先 --off 回退再重跑")
        return 1
    if not claw:
        claw = cur["init"]
    run = (RUN_CHILD if a.child else RUN_EXEC) % a.init_args
    with open(os.path.join(gen, "init"), "w", encoding="utf-8", newline="\n") as f:
        f.write(DISPATCH.replace("__CLAWBG__", CLAW_BG if a.claw_bg else "")
                          .replace("__E2RUN__", run).replace("__E2ARGS__", a.init_args))
    with open(os.path.join(gen, "init.claw"), "w", encoding="utf-8", newline="\n") as f:
        f.write(claw)
    with open(os.path.join(gen, "init.claw.rc"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CLAW_RC)
    with open(os.path.join(gen, "e2probe.sh"), "w", encoding="utf-8", newline="\n") as f:
        f.write(E2PROBE)
    with open(os.path.join(gen, "shadow-init"), "w", encoding="utf-8") as f:
        f.write("")
    for src_name, dst_name in (("system.build.prop", os.path.join(gen, "system.build.prop")),
                               ("vendor.build.prop", os.path.join(gen, "e2-vendor-build.prop"))):
        p = os.path.join(BLOBS, src_name)
        if not os.path.exists(p):
            print("!! 缺少 %s（先从 108 取回，见 BLOBS 上方注释）" % p)
            return 1
        shutil.copyfile(p, dst_name)
    # 真 init 第二阶段要读 /init.rc（镜像里只有 system/etc/init/hw/init.rc）
    hwrc = cur.get("system/etc/init/hw/init.rc")
    if not hwrc:
        print("!! 取不到 system/etc/init/hw/init.rc")
        return 1
    with open(os.path.join(gen, "init.rc"), "w", encoding="utf-8", newline="\n") as f:
        f.write(hwrc)
    print("  生成注入件：init(分发器) / init.claw(%d 行) / init.rc(%d 行) / "
          "system/etc/init/init.claw.rc / e2probe.sh / shadow-init" %
          (len(claw.splitlines()), len(hwrc.splitlines())))
    # 石头⑤（实测 06:45）：hw/init.rc 第 476~489 行给 4 个 boringssl 自检服务挂了
    # `reboot_on_failure reboot,boringssl-self-check-failed`。我们的 initrd 里没有 32 位运行时
    # ⇒ `cannot execv('/system/bin/boringssl_self_test32'): ENOENT` ⇒ 整台机器被 reboot 掉。
    # 石头⑦（实测 07:00）：linker64 实体化后 26 个 ENOENT 变成 **EACCES**，而内核侧打了
    # "selinux: SELinux: Loaded file_contexts"、ueventd 报 "Cannot get SELinux label on '/dev/xxx'"
    # ⇒ 内核 SELinux 开着但**没有策略**，init 在 execv 前 setexeccon("u:r:xxx:s0") 一律失败。
    #    （108 参照系是真容器、SELinux 关闭，我们没有这个条件 —— 这条推翻 T4 的"两边都 Disabled"。）
    # 两个开关都在 rc 里，所以按"整行精确匹配"摘除，别的字节不动。
    BO = "reboot_on_failure reboot,boringssl-self-check-failed"
    raw = {"system/etc/init/hw/init.rc": cur.get("system/etc/init/hw/init.rc") or "",
           "init.rc": cur.get("init.rc") or cur.get("system/etc/init/hw/init.rc") or ""}
    if a.strip_seclabel:
        for e in C.load(SRC, want=lambda n: n.endswith(".rc"))[0]:
            if e.data and e.name.startswith(("system/etc/init/", "vendor/etc/init/")):
                raw[e.name] = e.text
    rc_args = []
    for name in sorted(raw):
        body = raw[name]
        if not body:
            continue
        new = body
        if a.strip_seclabel:
            new = "\n".join(l for l in new.splitlines() if l.strip().split(" ")[0] != "seclabel") + "\n"
        if BO in new:
            new = "\n".join(l for l in new.splitlines() if l.strip() != BO) + "\n"
        if new == body:
            continue
        if name == "system/etc/init/hw/init.rc" and "service clawboot" not in new:
            # 实测⑫（07:24）：单独放一份 /system/etc/init/init.claw.rc，init **会 parse**（无解析错），
            # 但我们挂的 on boot / on early-boot / on post-fs-data 里的 `exec`/`start`
            # **一次都没执行**（原厂 rc 的 `starting service 'exec 2 (vdc volume abort_fuse)'` 有，
            # 我们的 exec 哨兵没有）⇒ "extra rc 把编排 import 回去"这条路在 Android 13 上不成立。
            # ⇒ 直接追加进 init 第二阶段的主 rc 本体（本就要为 boringssl 改写它，改动面不变大）。
            new = (new.rstrip("\n") +
                   "\n\n# === E2：claw 编排回挂（实测只有追加进主 rc 才生效）===\n" + CLAW_RC)
        p = os.path.join(gen, "rc_" + name.replace("/", "_"))
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(new)
        rc_args = rc_args + ["%s=%s" % (name, p)]
        print("  改写 %s：%d → %d 行（seclabel=%s boringssl自杀=%s）" %
              (name, len(body.splitlines()), len(new.splitlines()),
               "摘" if a.strip_seclabel else "留", "摘" if BO in body else "无"))
    if a.dry:
        print("  --dry：到此为止")
        return 0

    if not os.path.exists(BAK):
        shutil.copyfile(SRC, BAK)
        print("  备份原镜像 →", BAK)
    args = [sys.executable, INJECT, SRC, OUT,
            "init=%s" % os.path.join(gen, "init"),
            "+0755:init.claw=%s" % os.path.join(gen, "init.claw"),
            "+0644:init.rc=%s" % os.path.join(gen, "init.rc"),
            "+0644:system/etc/init/init.claw.rc=%s" % os.path.join(gen, "init.claw.rc"),
            "+0755:e2probe.sh=%s" % os.path.join(gen, "e2probe.sh"),
            "+0644:shadow-init=%s" % os.path.join(gen, "shadow-init"),
            "+0644:system/build.prop=%s" % os.path.join(gen, "system.build.prop"),
            "+0644:e2-vendor-build.prop=%s" % os.path.join(gen, "e2-vendor-build.prop")] + rc_args
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8",
                       errors="replace", env=env)
    print((r.stdout or "").strip()[-400:] or (r.stderr or "").strip()[-400:])
    if r.returncode != 0:
        print("!! 注入失败")
        return 1

    # 12 项镜像自检里有 4 项专查 **/init 里的编排内容**（thunder / harness / -Ximage / ARM 路径），
    # 而 E2 恰好把编排搬到了 /init.claw ⇒ 直接跑自检必然假红。
    # 正确做法：造一张「把 /init 换回原编排」的等价件跑自检 —— 全过 ⇒ 证明 E2 只改了 PID1 交接、
    # 编排内容逐字未动，才允许部署真件。
    equiv = os.path.join(WORK, "art_initrd_x64_shadow_equiv.gz")
    eq = subprocess.run([sys.executable, INJECT, OUT, equiv,
                         "init=%s" % os.path.join(gen, "init.claw")],
                        capture_output=True, text=True, encoding="utf-8", errors="replace",
                        env=env)
    if eq.returncode != 0:
        print("!! 等价件生成失败:", (eq.stdout or "")[-200:], (eq.stderr or "")[-200:])
        return 1
    chk = subprocess.run([sys.executable, CHECK, equiv], capture_output=True, text=True,
                         encoding="utf-8", errors="replace", env=env)
    out_chk = chk.stdout or ""
    tail = [l.strip() for l in out_chk.splitlines() if "FAIL" in l or "全部通过" in l]
    print("  编排等价件自检:", "; ".join(tail) or "(无输出)")
    if "全部通过" not in out_chk:
        print("!! 自检未过 ⇒ 不部署")
        return 1
    # 复核：直接把新镜像读回来，确认六件都在、/init 确实是分发器
    ents, _ = C.load(OUT, want=lambda n: n in WANT_IN_IMAGE)
    seen = {e.name: e for e in ents if e.name in WANT_IN_IMAGE}
    print("  注入复核:", "; ".join("%s=%dB/%o" % (n, seen[n].size, seen[n].mode & 0o7777)
                                  for n in sorted(seen)))
    miss = sorted(WANT_IN_IMAGE - set(seen) - {"init"})
    if miss:
        print("!! 新镜像里缺这些条目 ⇒ 不部署:", miss)
        return 1
    if MARK not in seen["init"].text:
        print("!! 新镜像的 /init 里没有分发器标记 ⇒ 不部署")
        return 1
    deploy(OUT)
    print("E2 shadow-PID1 已就位。下一步：起应用，然后读串口里的 [E2] 行。")
    print("回退：python tools/b1-build/shadow_init.py --off")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    raise SystemExit(main())
