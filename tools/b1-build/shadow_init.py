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
                 "e2probe.sh", "shadow-init", "system/build.prop", "e2-vendor-build.prop",
                 "e2-android-env.sh", "e2-cgroup.rc"}
# 108 参照系（Waydroid x86_64 / Android 13）里逐字取回的两份 build.prop：
#   ssh root@10.0.0.108 'mount -o ro,loop /var/lib/waydroid/images/system.img /tmp/e2pk; \
#     mount -o ro,loop /root/x86guest/vendor-ex/vendor.img /tmp/e2pv; \
#     cp /tmp/e2pk/system/build.prop /tmp/e2out/system.build.prop; \
#     cp /tmp/e2pv/build.prop /tmp/e2out/vendor.build.prop'
BLOBS = os.path.join(REPO, "JavaBridge", "qemu-src", "blobs", "e2")

DISPATCH = """#!/bin/busybox sh
__E2MARK__
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
    echo "[init] shadow-PID1：exec /system/bin/init __E2ARGS__"
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
            $BB ln $_a /system/bin/$_l 2>/dev/null || $BB cp $_a /system/bin/$_l
            $BB chmod 755 /system/bin/$_l
            echo "[init] $_l: 软链→实体 $($BB wc -c < /system/bin/$_l 2>/dev/null)B"
        else
            echo "[init] $_l: 未替换 islink=$($BB test -L /system/bin/$_l && echo Y || echo N) apex=$($BB test -f $_a && echo Y || echo N)"
        fi
    done
    # 石头⑧（07:44 诊断原文）：手跑 logd/servicemanager/hwservicemanager/lmkd/vdc 全部报
    #   CANNOT LINK EXECUTABLE "...": library "libc.so" not found: needed by main executable
    # —— /system/lib64 里 **libc / libdl / libdl_android / libm 这 4 个是指向
    # /apex/com.android.runtime/lib64/bionic/ 的软链**（其余 153 个都是实体），
    # 和 linker64 同一类病：过了 init 的 SetupMountNamespaces 就解析不到。
    # 解法用**硬链接**：同一个 initrd tmpfs ⇒ 零额外体积，也不需要 cp 一份 1.7MB。
    for _n in libc libdl libdl_android libm; do
        _t=/apex/com.android.runtime/lib64/bionic/$_n.so
        if $BB test -L /system/lib64/$_n.so && $BB test -f $_t; then
            $BB rm -f /system/lib64/$_n.so
            $BB ln $_t /system/lib64/$_n.so
            echo "[init] $_n.so 软链→硬链 $?"
        else
            echo "[init] $_n.so 未替换 islink=$($BB test -L /system/lib64/$_n.so && echo Y || echo N) 目标在=$($BB test -f $_t && echo Y || echo N)"
        fi
    done
    # 石头⑨（08:05 诊断原文）：手跑 zygote ⇒
    #   CANNOT LINK EXECUTABLE "/system/bin/app_process": library "libnativeloader.so" not found
    # —— ART 的库在 /apex/com.android.art/lib64 里，靠 apex 链接命名空间接入；而本次开机
    # linkerconfig 跑失败（`failed to execute linkerconfig: No such device` + 缺 VNDK apex），
    # /linkerconfig/ld.config.txt 还是烘进去的旧件 ⇒ 链接器在 system 命名空间里找不到 apex 库。
    # 实验性绕过：把 apex 里的 64 位库**硬链接**进 /system/lib64（同一个 tmpfs ⇒ 零额外体积；
    # 已有的名字一律不覆盖），让 zygote 先过了链接这一关，好把"下一道墙在哪"看清楚。
    $BB find /apex -maxdepth 3 -type f -name "*.so" -path "*/lib64/*" 2>/dev/null | while read _p; do
        _b=$($BB basename $_p)
        if [ ! -e /system/lib64/$_b ]; then
            $BB ln $_p /system/lib64/$_b 2>/dev/null
        fi
    done
    echo "[init] /system/lib64 现有 $($BB ls /system/lib64 2>/dev/null | $BB wc -l) 项；libnativeloader.so 在=$($BB test -e /system/lib64/libnativeloader.so && echo Y || echo N)"
    # 石头⑩（08:12）：logd / zygote / mediaextractor 全报 `received signal 6`，且
    # `code -1 (SI_QUEUE)` ⇒ 不是自己 abort()，而是 crash_handler 拦下段错误后再补刀；
    # 而 `/data/tombstones` **压根不存在**（探测 `ls` 输出为空）⇒ 唯一能拿到一手原因的落点被漏建。
    $BB mkdir -p /data/tombstones /data/anr /data/misc/logd /data/local/tmp /data/dalvik-cache /metadata
    $BB chmod 777 /data/tombstones /data/anr 2>/dev/null
    # 被包起来的服务大多不是 root（logd=user logd…），落不进 /tmp 就白包 —— 先把 /tmp 开成 777。
    $BB chmod 777 /tmp 2>/dev/null
    # /dev/kmsg 出厂是 crw------- root ⇒ 以 logd(uid 1036) 身份跑的包装层 echo 不进去
    # （实测：root 的 [ZYGOTE]/[LMKD] 都有，被包成 user logd 的 [LOGD] 一条也没有）。
    $BB chmod 666 /dev/kmsg 2>/dev/null
    # 石头⑯：logd/zygote 都是 SIGABRT 且**对 stderr 一个字不写**（它们的日志通道正是同样坏掉的 logd）
    # ⇒ 只能走 tombstone。但 tombstoned 的落点是 /data/tombstones，而 /data 在 init 的挂载命名空间里
    # （我们从外面读是 EIO）⇒ 把它挪到 /tmp：两边同一张 tmpfs，探测不必跨命名空间。
    $BB mkdir -p /tmp/tombstones
    $BB chmod 777 /tmp/tombstones
    $BB rm -rf /data/tombstones 2>/dev/null
    $BB ln -sf /tmp/tombstones /data/tombstones 2>/dev/null
    # 石头⑭（09:22 一手）：cpio 里 **/system/bin/logd 的 mode 是 0550**（AOSP 出厂 0755）
    #  ⇒ init 把它 setuid 成 logd(1036) 之后连自己都执行不了（wrapper 报 126）
    #  ⇒ 从头到尾没有任何日志，ART 的 abort 正文也就永远看不见。顺手把 /system/bin 补满执行位。
    $BB chmod 755 /system/bin/logd 2>/dev/null
    for _b in $($BB find /system/bin -maxdepth 1 -type f 2>/dev/null); do
        $BB test -x $_b || $BB chmod 755 $_b 2>/dev/null
    done
    # 石头⑮（09:28 一手）：连 **init 自己**都在报 `open() failed for /dev/cgroup_info/cgroup.rc`
    # （8.43s 两次），logd 随之 SIGABRT ⇒ 没有日志、也没有 ART 的 abort 正文。
    # 这文件是 init 用 libprocessgroup 生成的**二进制表**（version u32 + 条目数 7 + 定长记录），
    # 手写不如直接取参照系的：108 容器里 `/dev/cgroup_info/cgroup.rc` 共 400 B，原样搬来。
    # 顺手把表里点名的 cgroup2 与 freezer/pids 也补上（缺一个控制器就是另一条 CgroupMap 报错）。
    $BB mkdir -p /dev/cgroup_info /dev/stune
    $BB mount -t cgroup none /dev/stune -o cpu 2>/dev/null
    # 表里只留真挂上的控制器：cgroup2 挂得上才用带 UNIFIED 的 v2 版，否则 logd 照着表找不到的那两条就 abort。
    if [ "$($BB grep -c 'type cgroup2' /proc/mounts 2>/dev/null)" -gt 0 ]; then
        $BB cp /e2-cgroup-v2.rc /dev/cgroup_info/cgroup.rc 2>/dev/null
    else
        $BB cp /e2-cgroup-v1.rc /dev/cgroup_info/cgroup.rc 2>/dev/null
    fi
    $BB mount -t cgroup2 none /sys/fs/cgroup 2>/dev/null
    $BB mkdir -p /sys/fs/cgroup/unified 2>/dev/null
    $BB chmod 755 /dev/cgroup_info 2>/dev/null
    echo "[init] cgroup.rc=$($BB wc -c < /dev/cgroup_info/cgroup.rc 2>/dev/null)B cgroup2=$($BB grep -c 'type cgroup2' /proc/mounts)"
    # crash_dump64 在 /apex/com.android.runtime/bin 里，而 bionic 的 debuggerd 找的是
    # /system/bin/crash_dump64 ⇒ 又是软链/缺失那一类；不补上就没有 tombstone，正文照样拿不到。
    for _c in crash_dump64 crash_dump32; do
        _a=/apex/com.android.runtime/bin/$_c
        if [ ! -x /system/bin/$_c ] && [ -f $_a ]; then
            $BB rm -f /system/bin/$_c
            $BB ln $_a /system/bin/$_c 2>/dev/null || $BB cp $_a /system/bin/$_c
            $BB chmod 755 /system/bin/$_c
        fi
        echo "[init] $_c 可用=$($BB test -x /system/bin/$_c && echo Y || echo N)"
    done
    # 石头⑬（09:16 一手）：logd 自己报
    #   `logd: open() failed for /dev/cgroup_info/cgroup.rc: No such file or directory`
    #   `logd: libprocessgroup: CgroupMap::LoadRcFile failed`
    # 而 init 一路在喊 "cpuset cgroup controller is not mounted!"；lmkd 直接退出码 0、ART 主线程 SIGABRT
    # —— 三个"起来就死"很可能是**同一个根**：cgroup 没挂 ⇒ init 生成不出 /dev/cgroup_info/cgroup.rc
    #    ⇒ 所有用 libprocessgroup 的进程（logd/lmkd/ART）都没法继续。
    # /system/etc/cgroups.json 点名要四个 legacy 控制器目录，照它的清单挂：
    $BB mkdir -p /dev/blkio /dev/cpuctl /dev/cpuset /dev/memcg
    # 石头㉞（10:35 实测）：`cpu,cpuacct` 这一条挂失败（rc=255）**没有任何原文**，
    # 而 logd 的第一句就是 `failed to set background scheduling policy: No such file or directory`
    # 然后 SIGABRT —— 后台调度策略落在 /dev/cpuctl 上，挂不上就是它死的。
    # ⇒ 把 mount 的 stderr 收回来，并挨个试 cpu,cpuacct / cpu / cpuacct（内核可能只编了其中一个）。
    for _c in "blkio:/dev/blkio" "cpu,cpuacct|cpu|cpuacct:/dev/cpuctl" "cpuset:/dev/cpuset" "memory:/dev/memcg"; do
        _p=${_c##*:}
        _ok=
        for _o in $($BB echo "${_c%%:*}" | $BB tr '|' ' '); do
            _m=$($BB mount -t cgroup none $_p -o $_o 2>&1)
            if [ -z "$_m" ]; then _ok=$_o; break; fi
        done
        if [ -n "$_ok" ]; then
            echo "[init] cgroup $_p($_ok) 已挂"
        else
            echo "[init] cgroup $_p 挂失败: $_m（mount 数 $($BB grep -c cgroup /proc/mounts)）"
        fi
    done
    [ -f /modules/binder_linux.ko ] && $BB insmod /modules/binder_linux.ko devices=binder,hwbinder,vndbinder 2>&1
    # 石头㊱（10:50 实测）：servicemanager 与 hwservicemanager 死因同一句 ——
    #   `Binder driver '/dev/binder' could not be opened. Terminating: Opening '/dev/binder' failed: Permission denied`
    # 节点由 ueventd 在第二阶段 coldplomb 时创建（t=24.35 才起 ueventd），**晚于**分发器里的任何 chmod，
    # 所以放一个常驻小工：节点一出现且权限不是 0666 就补上去（AOSP 出厂即 crw-rw-rw-），并只报一次变化。
    $BB sh -c 'while :; do for _d in /dev/binder /dev/hwbinder /dev/vndbinder; do
        [ -c $_d ] || continue
        _m=$($BB ls -l $_d | $BB cut -c1-10)
        [ "$_m" = "crw-rw-rw-" ] && continue
        $BB chmod 666 $_d && echo "<3>[init] $_d 权限 $_m -> 666" > /dev/kmsg
    done; $BB sleep 2; done' &
    # 石头⑫（09:02 一手）：64 位 zygote 起来后 **退出码=0、一个字都不印** —— hw/init.rc 里
    # 只有 1 行 export，BOOTCLASSPATH / SYSTEMSERVERCLASSPATH 全缺。108 参照系的这两个值是
    # lxc 容器环境喂进 init 的（grep 遍 /system/etc 也没有）。
    # ⚠ 但"exec 之前 export ⇒ 服务就能拿到"是**错的**（10:36 实测）：第二阶段 init 进来先 clean_env()，
    #    真正通道是下面按当前环境生成的 /init.environ.rc（rc 的 export 只认两个参数、且必须挂在段头下面）。
    if [ -f /e2-android-env.sh ]; then
        . /e2-android-env.sh
        echo "[init] 环境已补：BOOTCLASSPATH $($BB echo -n $BOOTCLASSPATH | $BB wc -c)B SYSTEMSERVERCLASSPATH $($BB echo -n $SYSTEMSERVERCLASSPATH | $BB wc -c)B"
    fi
    # ── N1-1 取证（2026-10-02）：shadow 形态下 init 起服务时会按 rc 的 socket 语句**重建**
    # /dev/socket/logdw ⇒ 早先 bind 的 fakelogd 持有的是被换掉的旧 inode，一条也收不到
    # （实测 09:31 开机 [logd] 输出 0 条）。监视 inode 变化，一变就杀旧实例重新 bind ——
    # ART 的 LOG(FATAL) 原文走的就是 logdw，拿到它 = 拿到 abort 原文。
    $BB sh -c 'L=; FP=; while :; do I=$(ls -i /dev/socket/logdw 2>/dev/null); if [ -n "$I" ] && [ "$I" != "$L" ]; then L=$I; kill -9 $FP 2>/dev/null; /fakelogd & FP=$!; fi; sleep 1; done' &
    # 石头㉜（10:22 一手）：串口里每个服务都在喊
    #   init: Unable to read config file '/init.environ.rc': open() failed: No such file or directory
    # 而 /init.environ.rc 正是**第一阶段**把 PID1 环境逐条写成 `export NAME value` 的产物 ——
    # 我们跳过了第一阶段，所以没人写它；而第二阶段的 init 进来先 clean_env()，
    # 于是"exec 之前 export 的东西会传给服务"这条**只在 child 形态成立**，exec 形态下全被抹掉
    # ⇒ ANDROID_DATA 仍为空、/etc/task_profiles.json 仍是空路径拼出来的。
    # 官方通道就在眼前：按当前环境把这份文件补出来（import 列表已经带着它，parse 期即生效）。
__ENVEXPORT__
    $BB env > /tmp/e2-env.txt
    : > /init.environ.rc
    echo "on early-init" > /init.environ.rc
    while IFS= read -r _kv; do
        _n=${_kv%%=*}
        [ "$_n" = "$_kv" ] && continue
        _v=${_kv#*=}
        [ -z "$_v" ] && continue
        # ① 名字必须是合法标识符；② 值不能含空白 —— rc 的 export 只吃两个参数，
        #    多一个词就变成 "Invalid section keyword"（实测 10:35 那一轮 26 行全被拒）。
        $BB printf '%s\n' "$_v" | $BB grep -q '[[:space:]]' && continue
        $BB printf '%s\n' "$_n" | $BB grep -q '^[A-Za-z_][A-Za-z0-9_]*$' || continue
        echo "    export $_n $_v" >> /init.environ.rc
    done < /tmp/e2-env.txt
    echo "[init] /init.environ.rc $($BB wc -c < /init.environ.rc)B 条数=$($BB wc -l < /init.environ.rc)"
__CLAWBG__
__E2RUN__
fi
exec $BB sh /init.claw
""".replace("__E2MARK__", MARK)

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

# 实测 08:44（zygote 包了 stderr→kmsg 之后拿到的**一手**原因）：
#   [ZYGOTE] ANDROID_DATA environment variable unset  ⇒ app_process64 立刻 SIGABRT
# 真 init 不会替服务导出 ANDROID_*（hw/init.rc 原厂只有 1 行 export），第二阶段进来还先 clean_env()。
# 石头㉛/㉜ 的判读修正（10:44 实测）：`early-init` **在第二阶段确实会 fire**
#   （`processing action (early-init) from (/system/etc/init/hw/init.rc:15)` @26.21，早于 logd @27.31），
#   而我换上去的 `on init` 拖到 t=58.8 才 fire —— 对第一个实例来说太晚。
# ⇒ 主通道是分发器按环境生成的 /init.environ.rc（挂在 `on early-init` 下，parse 期即生效），
#   这一段留作同触发的备份（两处同一个值，重复 export 无副作用）。
ENV_RC = """# === E2：给所有服务补 ANDROID_* 环境（实测缺它 app_process64 直接 abort）===
on early-init
    export ANDROID_ROOT /system
    export ANDROID_DATA /data
    export ANDROID_STORAGE /storage
    export ANDROID_ART_ROOT /apex/com.android.art
    export ANDROID_I18N_ROOT /apex/com.android.i18n
    export ANDROID_TZDATA_ROOT /apex/com.android.tzdata
    export EXTERNAL_STORAGE /sdcard
    export DOWNLOAD_CACHE /data/download_cache
    export TMPDIR /data/local/tmp
    export PATH /product/bin:/apex/com.android.runtime/bin:/apex/com.android.art/bin:/system_ext/bin:/system/bin:/system/xbin:/odm/bin:/vendor/bin:/vendor/xbin
"""

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
# PID1 换成真 init 之后，属性区与 /data 都在**它的挂载命名空间**里；我们的探测是在
# `exec` 之前 fork 的 ⇒ 数得到 /dev/__properties__ 的文件、getprop 却输出 0 行，
# /data/tombstones 还报 EIO（实测 08:16/08:20）。busybox 带 nsenter applet ⇒
# 进 PID1 的 mount ns 问出来的才是真值（判据 `init.svc.*` 必须这么读）。
gp() { $BB nsenter -t 1 -m -- /system/bin/getprop "$@" 2>/dev/null; }
# 用法：nsh <绝对路径或 applet…>。⚠ 早先写成 `-- $BB "$@"`，于是
# `nsh /system/bin/logcat …` 变成 `busybox /system/bin/logcat` ⇒ "applet not found"（实测 09:32）。
nsh() { $BB nsenter -t 1 -m -- "$@" 2>&1; }
line() {
    T=$($BB cut -d. -f1 /proc/uptime 2>/dev/null)
    SVC=$(gp | $BB grep -c "init.svc")
    GP=$(gp | $BB wc -l)
    PROPS=$(ls /dev/__properties__ 2>/dev/null | $BB wc -l)
    echo "<4>[E2] t=${T}s init.svc=${SVC:-?} props=${PROPS:-?} getprop可读=${GP:-?} zygote=$(gp init.svc.zygote) boot=$(gp sys.boot_completed) sdk=$(gp ro.build.version.sdk) hw=$(gp ro.hardware) svcmgr=$(gp servicemanager.ready)" > /dev/kmsg
}
if [ "$1" = once ]; then line; exit 0; fi
# 诊断档：E4 之前必须回答"服务为什么起来就退（status 1 / 127）"。
# 直接在探测里手跑那几个二进制，把 linker/内核给的**原文**打到 kmsg（init 只报退出码，不报原因）。
diag() {
    # ⚠ 不能用 `timeout … | head`：守护型进程（hwservicemanager）会让管道在 timeout 之后
    #   仍等输出，实测整条 diag 卡死在第 3 个（07:59 那次只打出两条）。
    #   改成"后台跑 + 落文件 + 定时 kill + 读文件"，每条最多 5s，互不牵连。
    run1() {
        N=$1; shift
        $BB rm -f /tmp/d.$N
        $BB timeout -s 9 4 "$@" > /tmp/d.$N 2>&1 &
        P=$!
        $BB sleep 5
        $BB kill -9 $P 2>/dev/null
        echo "<3>[E2] 手跑 $N → $($BB head -6 /tmp/d.$N 2>/dev/null | $BB tr '\n' '~' | $BB cut -c1-300)" > /dev/kmsg
    }
    echo "<3>[E2] getprop 的 stderr: $($BB nsenter -t 1 -m -- /system/bin/getprop 2>&1 >/dev/null | $BB head -2 | $BB tr '\n' '~' | $BB cut -c1-140)" > /dev/kmsg
    echo "<3>[E2] linkerconfig: $(nsh /bin/busybox ls /linkerconfig | $BB tr '\n' ' ')" > /dev/kmsg
    for b in logd servicemanager hwservicemanager lmkd; do
        run1 $b $BB nsenter -t 1 -m -- /system/bin/$b
    done
    # E4 的前题就是 zygote：init 现在每 5s 起它一次、每次 status 1 就退。
    # 把 rc 里那行命令原样拿来手跑一次，把 ART 自己吐的**第一手原因**送进串口。
    # ⚠ `service zygote <cmd> <args…>` ⇒ 命令从第 3 个字段开始（第一次跑成 f2- 把服务名当成了程序名）。
    Z=$($BB grep -h -m1 "^service zygote " /system/etc/init/hw/init.zygote*.rc 2>/dev/null | $BB cut -d" " -f3-)
    echo "<3>[E2] zygote 命令: $Z" > /dev/kmsg
    run1 zygote $BB nsenter -t 1 -m -- $Z
    # zygote 现在能过链接期、改成 signal 6（SIGABRT）——ART 的 abort 原文只落在 tombstone 里。
    # /data 挂在 init 的命名空间内（我们在 exec 之前 fork ⇒ 看到的是坏视图，报 EIO），
    # 所以 tombstone 也必须进 ns 读。
    _tl=$(nsh /bin/busybox ls -t /data/tombstones 2>/dev/null | $BB head -1)
    echo "<3>[E2] tombstones: $(nsh ls /data/tombstones | $BB tr '\n' ' ' | $BB cut -c1-160)" > /dev/kmsg
    echo "<3>[E2] 最新 tombstone($_tl) 摘要: $(nsh /bin/busybox head -c 300 /data/tombstones/$_tl 2>&1 | $BB tr '\n' '~')" > /dev/kmsg
    echo "<3>[E2] /data 挂载: $(nsh /bin/busybox grep ' /data ' /proc/mounts | $BB tr '\n' '~' | $BB cut -c1-200)" > /dev/kmsg
    # ⚠ 遗留待判（本轮没解掉）：进 init 命名空间后 `getprop` 仍输出 0 行，而
    #   `ls /dev/__properties__` = 264、init 自己 `Setting property 'ro.build.fingerprint'` 成功。
    #   三个候选，下次按这条顺序一次判掉：
    #     (a) nsenter 自己失败被我 2>/dev/null 吞了 ⇒ 下面这行会把 stderr 原文打出来；
    #     (b) 客户端 mmap 属性文件被 SELinux 拒（我们进程没标签，hwservicemanager 也报过
    #         `Using old property service protocol ("ro.property_service.version" is not set)`）；
    #     (c) `property_info` 这张 trie 是空的 ⇒ foreach 直接 0 条。
    echo "<3>[E2] nsenter 自检: $(nsh echo 在-init-ns) ｜ id: $($BB id 2>&1 | $BB cut -c1-70)" > /dev/kmsg
    echo "<3>[E2] property_info: $(nsh ls -l /dev/__properties__/property_info 2>&1 | $BB cut -c1-90) ｜ serial: $(nsh ls -l /dev/__properties__/properties_serial 2>&1 | $BB cut -c1-90)" > /dev/kmsg
    echo "<3>[E2] ns 里的 getprop stderr: $($BB nsenter -t 1 -m -- /system/bin/getprop 2>&1 >/dev/null | $BB head -2 | $BB tr '\n' '~' | $BB cut -c1-140) ｜ 行数: $($BB nsenter -t 1 -m -- /system/bin/getprop 2>/dev/null | $BB wc -l)" > /dev/kmsg
    # logd 修好之后才有的读法：向 logd 直接拉崩溃缓冲与 zygote 相关日志（nsh = 进 init 的挂载命名空间）。
    # tombstone 里有 `Abort message: '……'` —— 这一轮要的就是那一行。
    # ⚠ 这三条必须走 nsh：init 会往 /tmp、/data 上盖自己的 tmpfs，
    #   我们在 exec 之前 fork 的探测看这两个路径只会得到 EIO（实测 09:42 就这样白读一轮）。
    _ts=$(nsh /bin/busybox ls -t /tmp/tombstones 2>/dev/null | $BB head -1)
    echo "<3>[E2] /tmp/tombstones: $(nsh /bin/busybox ls /tmp/tombstones 2>&1 | $BB tr '
' ' ' | $BB cut -c1-120)" > /dev/kmsg
    echo "<3>[E2] tombstone($_ts) Abort message: $(nsh /bin/busybox grep -a -m1 'Abort message' /tmp/tombstones/$_ts 2>&1 | $BB cut -c1-300)" > /dev/kmsg
    echo "<3>[E2] tombstone 前 6 行: $(nsh /bin/busybox head -6 /tmp/tombstones/$_ts 2>&1 | $BB tr '
' '~' | $BB cut -c1-300)" > /dev/kmsg
    for _w in LOGD ZYGOTE LMKD; do
        echo "<3>[E2] 包装落点 /tmp/$_w.out: $($BB head -c 300 /tmp/$_w.out 2>&1 | $BB tr '
' '~')" > /dev/kmsg
    done
    echo "<3>[E2] logcat -b crash: $(nsh /system/bin/logcat -d -b crash -t 25 2>&1 | $BB tr '\n' '~' | $BB cut -c1-300)" > /dev/kmsg
    echo "<3>[E2] logcat zygote: $(nsh /system/bin/logcat -d -s Zygote ART SystemServerActivityManager 2>&1 | $BB tr '\n' '~' | $BB cut -c1-300)" > /dev/kmsg
    # ── N1-2/N1-3 采集（2026-10-02）：与 108 zygote64 逐字 diff 的 guest 侧原始数据 ──
    # 属性差：getprop | grep dalvik 逐行进 kmsg（<4>，行数少，直接全文）
    echo "<3>[N1] dalvik 属性条数: $(gp | $BB grep -c dalvik)" > /dev/kmsg
    gp | $BB grep dalvik | $BB sort | while read -r _l; do echo "<4>[N1-DP] $_l" > /dev/kmsg; done
    # 环境差：guest 侧 zygote64 的 environ / cmdline / maps 里的 art apex（拿不到就明说，别装）
    _zp=$($BB pidof zygote64 2>/dev/null || $BB pidof zygote 2>/dev/null)
    if [ -n "$_zp" ]; then
        echo "<3>[N1] guest zygote pid=$_zp cmdline=$($BB tr '\\0' ' ' < /proc/$_zp/cmdline 2>&1 | $BB cut -c1-120)" > /dev/kmsg
        $BB tr '\\0' '\\n' < /proc/$_zp/environ 2>/dev/null | $BB sort | while read -r _l; do echo "<4>[N1-ENV] $_l" > /dev/kmsg; done
        $BB grep 'apex/com.android.art' /proc/$_zp/maps 2>/dev/null | $BB sed 's/.*\\//\\//' | $BB sort -u | while read -r _l; do echo "<4>[N1-MAPS] $_l" > /dev/kmsg; done
        # tombstone 路线：zygote 活着的窗口里抓一次裸回溯（abort 原文若 fakelogd 仍收不到就靠它）
        $BB timeout -s 9 6 $BB nsenter -t 1 -m -- /system/bin/debuggerd -b $_zp > /tmp/d.zb 2>&1
        echo "<3>[N1] debuggerd -b zygote 前 240B: $($BB head -c 240 /tmp/d.zb 2>&1 | $BB tr '\n' '~')" > /dev/kmsg
    else
        echo "<3>[N1] zygote 不在（pidof 空）——environ/maps 取证跳过本轮" > /dev/kmsg
    fi
    # 上一轮 `ls /data/tombstones` 报 **Input/output error** ⇒ /data 这个挂载点本身在坏，
    # 而 logd/tombstoned/zygote 全都要写 /data ⇒ 这才是"起来就 SIGABRT"的候选根因。
    # 先把 /data 到底是什么、挂在哪、能不能列，一次性问清楚。
    echo "<3>[E2] /data 挂载: $($BB grep ' /data ' /proc/mounts 2>&1 | $BB tr '\n' '~' | $BB cut -c1-200)" > /dev/kmsg
    echo "<3>[E2] /data ls: $($BB ls /data 2>&1 | $BB tr '\n' ' ' | $BB cut -c1-160)" > /dev/kmsg
    echo "<3>[E2] /data stat: $($BB stat -c '%m %T %s' /data 2>&1) df: $($BB df /data 2>&1 | $BB tail -1)" > /dev/kmsg
}
if [ "$1" = diag ]; then diag; exit 0; fi
i=0
while [ $i -lt 24 ]; do
    line
    [ $i -eq 2 ] && diag
    $BB sleep 5
    i=$((i+1))
done
echo "<4>[E2] probe 结束" > /dev/kmsg
"""

# init 起服务时 stdio 接到 /dev/null（cmdline 没有 init_debug，而我们改不了宿主 -append），
# 所以 zygote 的 ART abort 原文既进不了 logd（logd 自己就 127）也上不了串口 ——
# 这一层包装只为把 stderr 逐行搬进 /dev/kmsg，拿到一手原因。
ZYGWRAP = """#!/bin/busybox sh
BB=/bin/busybox
TAG=$1
shift
# ⚠ 不能写成 `"$@" | while read …`：那样 sh 的退出码是 while 循环的 0，
#   init 只会看到 "exited with status 0" ⇒ 真崩溃被伪装成正常退出（实测 08:52 就这样被骗过一次）。
#   落文件 + 跑完再把原文送串口，退出码才保真。
"$@" > /tmp/$TAG.out 2>&1
RC=$?
# 一手证据：这个包装进程的环境**就是 init 给服务的环境**（rc 的 export 到底有没有落到服务上，
# 看这一行就有答案，不用再靠 "/etc/task_profiles.json" 这种间接现象猜）。
echo "<3>[$TAG] env: AR=[$ANDROID_ROOT] AD=[$ANDROID_DATA] BCP=$($BB printf %s "$BOOTCLASSPATH" | $BB wc -c)B PATH=[$(echo -n $PATH | $BB cut -c1-40)]" > /dev/kmsg
echo "<3>[$TAG] 退出码=$RC ｜ 输出: $($BB head -c 320 /tmp/$TAG.out 2>&1 | $BB tr '\n' '~')" > /dev/kmsg
# 尾部另送一次：ART 的 `Abort message:` 在文件最后几行，head -c 320 够不着
# （实测 logd 的头部只有 "Aborted" 一个词）。多行进 kmsg 会被内核拆成多条记录，
# 后续行没有 <3> 前缀 ⇒ 靠分发器把 console_loglevel 抬到 7 才看得见。
echo "<3>[$TAG] 尾部: $($BB tail -c 420 /tmp/$TAG.out 2>&1)" > /dev/kmsg
exit $RC
"""


def cgroup_rc(entries):
    """参照系倒推出的二进制表布局：u32 version + u32 count + 每条 {u32 mode, name[28], path[24]} = 56B/条。

    mode：1=LEGACY（cgroup v1 控制器）、2=UNIFIED（cgroup2）。
    我们这内核 cgroup2 挂不上（分发器实测 `cgroup2=0`），而 logd/libprocessgroup 会**照着这张表**
    去找控制器 ⇒ 表里写了我们没有的那两条，它就 abort（实测 logd 退出码 134 且零输出）。
    所以按"实际挂上了什么"生成两版，分发器按 `grep type cgroup2 /proc/mounts` 二选一。
    """
    import struct
    out = struct.pack("<II", 1, len(entries))
    for mode, name, path in entries:
        out += struct.pack("<I", mode) + name.encode().ljust(28, b"\0") + path.encode().ljust(24, b"\0")
    return out


LEGACY_CGROUPS = [(1, "blkio", "/dev/blkio"), (1, "cpu", "/dev/cpuctl"), (1, "cpuset", "/dev/cpuset"),
                  (1, "memory", "/dev/memcg"), (1, "schedtune", "/dev/stune")]
UNIFIED_CGROUPS = [(2, "cgroup2", "/sys/fs/cgroup"), (2, "freezer", "/sys/fs/cgroup")]


def env_exports_sh():
    """把 ENV_RC 的 `export 名字 值` 换成 shell 的 `export 名字=值`。

    单一事实来源：/init.environ.rc 与 rc 的 `on init` 补环境用的是同一张表，
    改一处两边都跟着变（第一阶段被跳过 ⇒ 没人写 /init.environ.rc，只能自己按环境生成）。
    """
    out = []
    for line in ENV_RC.splitlines():
        w = line.strip().split()
        if len(w) == 3 and w[0] == "export":
            out.append("    export %s=%s" % (w[1], w[2]))
    return "\n".join(out)


def drop_service_stanza(text, names):
    """整段删掉 `service <名> …`（声明行 + 其后所有缩进行）。

    用途见 --no-zygote32：镜像里没有 init.zygote64.rc，只能从 64_32 那份里把 32 位段摘掉。
    段边界按 rc 的书写惯例判 —— 缩进行属于当前段，遇到顶格的段头（service/on/import…）就结束。
    """
    kept, skip = [], False
    for line in text.splitlines():
        w = line.split()
        if skip:
            if line[:1] in (" ", "\t"):
                continue
            skip = False
        if len(w) > 1 and w[0] == "service" and w[1] in names:
            skip = True
            continue
        kept.append(line)
    return "\n".join(kept) + "\n"


def neuter_zygote_restarts(text):
    """把缩进层里的 `restart zygote*` / `stop zygote*` 改成无害的 `trigger claw-noop`，返回 (文本, 条数)。

    实测 10:50：netd 每 5s 退出码 1，而它的 `onrestart` 第一条就是 `restart zygote` ——
      148.328514 Service 'netd' (pid 4382) exited with status 1
      148.571525 Sending signal 9 to service 'zygote' (pid 4377) process group...
      148.820852 Command 'restart zygote' action=onrestart (<Service 'netd' onrestart>:1) took 249ms
    ⇒ zygote 每次刚活就被邻居的联动打死（`init.svc.zygote` 在 running/restarting/stopping 之间抖）。
    只改**缩进行**（onrestart 里的命令都是缩进的），不动 `service zygote` 本体，也不改 rc 的段落结构，
    所以不会像"删行"那样把后面的缩进命令变成孤儿。
    """
    out, n = [], 0
    for line in text.splitlines():
        w = line.split()
        # 实测语法：`onrestart restart zygote` 是**一行**（onrestart 不是段头，后面直接跟命令）
        if line[:1] in (" ", "\t") and len(w) == 3 and w[0] == "onrestart" and w[1] in ("restart", "stop") \
                and w[2].startswith("zygote"):
            indent = line[:len(line) - len(line.lstrip())]
            out.append(indent + "onrestart trigger claw-noop")
            n += 1
            continue
        out.append(line)
    return "\n".join(out) + "\n", n


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
    ap.add_argument("--no-rc-guard", action="store_true",
                    help="摘掉所有 rc 里的 reboot_on_failure（真机失败即重启的安全闸；实验里它只会把进度挡住）")
    ap.add_argument("--no-critical", action="store_true",
                    help="摘掉所有 rc 服务的 critical（critical 进程连死 4 次 = 整台机器 reboot，"
                         "实验里等于每 70s 把证据抹一遍）")
    ap.add_argument("--no-zygote32", action="store_true",
                    help="删掉 service zygote_secondary 整段（镜像里没有 libandroid_runtime.so 的 32 位闭包，"
                         "它秒退并用 onrestart 把好好的 64 位 zygote 一起打死）")
    ap.add_argument("--protect-zygote", action="store_true",
                    help="把 rc 里 `restart zygote` / `stop zygote*` 这类联动改成无害命令 —— "
                         "netd/media/surfaceflinger 每 5s 死一次，它们的 onrestart 会把刚活下来的 zygote SIGKILL")
    ap.add_argument("--zygote-log", action="store_true",
                    help="= --wrap zygote,zygote_secondary：把服务的 stderr 落文件再送串口，并保住真实退出码")
    ap.add_argument("--wrap", default="",
                    help="逗号分隔的服务名，逐个包上 /zygwrap.sh（用来拿 logd/servicemanager 的 linker 原文）")
    ap.add_argument("--claw-bg", action="store_true",
                    help="exec 真 init 之前先在后台挂上原编排 + 探测器（rc 触发器实测不生效，用它替代）")
    ap.add_argument("--no-deploy", action="store_true",
                    help="只产出 .zwork/art_initrd_x64_shadow.gz，不写 SRC/RUN"
                         "（别的会话在用现镜像时用它做私跑）")
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
    # rc 的改写要从**没有被 E2 追加过**的那一份出发（否则第二轮会"在上一版结果上再摘一遍/再追加一遍"）。
    # 但"哪一份没被改过"不能写死成备份件：实测 10:04 那轮 SRC 是 T1 的新镜像（rc 干净），
    # 脚本却固定拿 06:00 的 BAK 当 rc 基线 —— 等于把两代内容混在一张镜像里。
    # ⇒ 按镜像里自己的 E2 注释头判：SRC 的 rc 干净就用 SRC，只有它已经带追加段才退回 BAK。
    already = "=== E2：claw 编排回挂" in (cur.get("system/etc/init/hw/init.rc") or "")
    BASE = BAK if (already and os.path.exists(BAK)) else SRC
    base, _ = extract(BASE, {"system/etc/init/hw/init.rc", "init.rc"})
    print("rc 基线:", "备份件（未改造）" if BASE == BAK else "现镜像（其 rc 未带 E2 追加段）")
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
    # 石头⑪（08:29 实测）：编排里那句 `propinit` 会 remove()+mkdir() **重建 /dev/__properties__**，
    # 于是真 init 写的 `properties_serial` 被抹掉 —— property_info(74,112B) 还在、serial 没了，
    # 之后所有客户端 `getprop` 都输出 0 行（nsenter 进 init 的命名空间也一样，已排除命名空间因素）。
    # E2 形态下属性区归真 init 所有，这句必须跳过（非 shadow 路径仍按原样跑）。
    claw = "\n".join(("# [E2 跳过：属性区归真 init] " + l
                      if l.strip().startswith("[ -x /system/bin/propinit ]") else l)
                     for l in claw.splitlines()) + "\n"
    # ── N1 稳定性（2026-10-02 实测 09:40 开机）：shadow 形态下编排会被 claw-bg（exec 前后台）
    # 与 rc 的 clawboot 服务（post-fs-data / early-boot / boot / boot_completed /
    # hwservicemanager.ready 一堆触发器，服务退出后 start 会**再次拉起**）各跑一遍 ——
    # 实测 [astack] 解包完成 ×7、ENOSPC ×2、servicemanager 4 实例 ⇒ tmpfs 挤爆、
    # 属性区被冲掉（props=0 / getprop 0 行的"坏开机"）。编排必须幂等：守卫文件只放行第一遍。
    _shebang, _rest = claw.split("\n", 1)
    claw = (_shebang + "\n" +
            "if [ -e /tmp/claw.done ]; then exit 0; fi\n"
            "/bin/busybox touch /tmp/claw.done\n"
            "# [N1 守卫] 上面的 exit 0 之外的第二遍编排一概不跑（见 shadow_init.py 注释）\n"
            + _rest)
    run = (RUN_CHILD if a.child else RUN_EXEC) % a.init_args
    with open(os.path.join(gen, "init"), "w", encoding="utf-8", newline="\n") as f:
        f.write(DISPATCH.replace("__CLAWBG__", CLAW_BG if a.claw_bg else "")
                          .replace("__E2RUN__", run).replace("__E2ARGS__", a.init_args)
                          .replace("__ENVEXPORT__", env_exports_sh()))
    with open(os.path.join(gen, "init.claw"), "w", encoding="utf-8", newline="\n") as f:
        f.write(claw)
    with open(os.path.join(gen, "init.claw.rc"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CLAW_RC)
    with open(os.path.join(gen, "e2probe.sh"), "w", encoding="utf-8", newline="\n") as f:
        f.write(E2PROBE)
    with open(os.path.join(gen, "zygwrap.sh"), "w", encoding="utf-8", newline="\n") as f:
        f.write(ZYGWRAP)
    with open(os.path.join(gen, "e2-cgroup-v1.rc"), "wb") as f:
        f.write(cgroup_rc(LEGACY_CGROUPS))
    with open(os.path.join(gen, "e2-cgroup-v2.rc"), "wb") as f:
        f.write(cgroup_rc(LEGACY_CGROUPS + UNIFIED_CGROUPS))
    with open(os.path.join(gen, "shadow-init"), "w", encoding="utf-8") as f:
        f.write("")
    for src_name, dst_name in (("system.build.prop", os.path.join(gen, "system.build.prop")),
                               ("vendor.build.prop", os.path.join(gen, "e2-vendor-build.prop")),
                               ("android-env.sh", os.path.join(gen, "e2-android-env.sh")),
                               ("cgroup.rc", os.path.join(gen, "e2-cgroup.rc"))):
        p = os.path.join(BLOBS, src_name)
        if not os.path.exists(p):
            print("!! 缺少 %s（先从 108 取回，见 BLOBS 上方注释）" % p)
            return 1
        shutil.copyfile(p, dst_name)
    # 真 init 第二阶段要读 /init.rc（镜像里只有 system/etc/init/hw/init.rc）
    hwrc = base.get("system/etc/init/hw/init.rc")
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
    raw = {"system/etc/init/hw/init.rc": base.get("system/etc/init/hw/init.rc") or "",
           "init.rc": base.get("init.rc") or base.get("system/etc/init/hw/init.rc") or ""}
    WRAP = set(x.strip() for x in (a.wrap + (",zygote,zygote_secondary" if a.zygote_log else "")).split(",") if x.strip())
    if a.strip_seclabel or a.no_rc_guard or a.no_critical or a.no_zygote32 or a.protect_zygote or WRAP:
        for e in C.load(BASE, want=lambda n: n.endswith(".rc"))[0]:
            if e.data and e.name.startswith(("system/etc/init/", "vendor/etc/init/")):
                raw[e.name] = e.text
    if WRAP:
        # 只换 `service <名> …` 那一行的**命令**，服务名与后续选项（socket/user/group/critical）全留。
        for name in list(raw):
            if "init/" not in name and "init.rc" not in name:
                continue
            out2 = []
            for l in raw[name].splitlines():
                w = l.split()
                if len(w) > 2 and w[0] == "service" and w[1] in WRAP:
                    # 标签必须每个服务唯一：上一版截成 6 字符 ⇒ zygote 与 zygote_secondary 共用
                    # /tmp/ZYGOTE.out，64 位那份的输出被 32 位的报错刷掉，白读一轮。
                    l = "service %s /bin/busybox sh /zygwrap.sh %s %s" % (w[1], w[1].upper(), " ".join(w[2:]))
                out2.append(l)
            raw[name] = "\n".join(out2) + "\n"
            print("  zygote 包了一层 stderr→kmsg：%s" % name)
    rc_args = []
    for name in sorted(raw):
        body = raw[name]
        if not body:
            continue
        new = body
        drops = set()
        if a.strip_seclabel:
            drops.add("seclabel")
        if a.no_rc_guard:
            drops.add("reboot_on_failure")
        if a.no_critical:
            # 石头㉞（10:44 实测）：`init: critical process 'hwservicemanager' exited 4 times
            # before boot completed` ⇒ `reboot: Restarting system with command 'bootloader'`
            # —— 真机上这是对的（起不来就该重启），但实验里它每 70s 把整场开机和所有证据一起抹掉。
            drops.add("critical")
        if drops:
            new = "\n".join(l for l in new.splitlines()
                            if l.strip().split(" ")[0] not in drops) + "\n"
        elif BO in new:                       # 默认只摘 boringssl 那一条自杀闸
            new = "\n".join(l for l in new.splitlines() if l.strip() != BO) + "\n"
        if a.no_zygote32 and "service zygote_secondary " in new:
            # 石头㉝（10:22 实测）：zygote_secondary 是 `CANNOT LINK EXECUTABLE app_process32:
            # library "libandroid_runtime.so" not found`（镜像里只有 64 位闭包）⇒ 退出码 1，
            # 而它的 onrestart 链是 `restart zygote / restart audioserver / …`，
            # 于是**每一轮都把已经活着的 64 位 zygote 一起 SIGKILL**（串口实证
            # "Sending signal 9 to service 'zygote'" 紧跟在 secondary 的 onrestart 后面）
            # ⇒ zygote 永远停在 restarting。整段摘掉才是收敛，不是包装能救的。
            new = drop_service_stanza(new, {"zygote_secondary"})
            print("  摘掉 32 位 zygote 整段：%s" % name)
        if a.protect_zygote:
            new, kn = neuter_zygote_restarts(new)
            if kn:
                print("  解除 zygote 连动 %d 条：%s" % (kn, name))
        if new == body and not WRAP:
            continue
        if name == "system/etc/init/hw/init.rc" and "service clawboot" not in new:
            # 实测⑫（07:24）：单独放一份 /system/etc/init/init.claw.rc，init **会 parse**（无解析错），
            # 但我们挂的 on boot / on early-boot / on post-fs-data 里的 `exec`/`start`
            # **一次都没执行**（原厂 rc 的 `starting service 'exec 2 (vdc volume abort_fuse)'` 有，
            # 我们的 exec 哨兵没有）⇒ "extra rc 把编排 import 回去"这条路在 Android 13 上不成立。
            # ⇒ 直接追加进 init 第二阶段的主 rc 本体（本就要为 boringssl 改写它，改动面不变大）。
            new = (new.rstrip("\n") +
                   "\n\n# === E2：claw 编排回挂（实测只有追加进主 rc 才生效）===\n" + CLAW_RC +
                   "\n" + ENV_RC)
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
            "+0644:system/etc/init/init.claw.rc=%s" % os.path.join(gen, "init.claw.rc"),
            "+0755:e2probe.sh=%s" % os.path.join(gen, "e2probe.sh"),
            "+0644:shadow-init=%s" % os.path.join(gen, "shadow-init"),
            "+0644:system/build.prop=%s" % os.path.join(gen, "system.build.prop"),
            "+0755:zygwrap.sh=%s" % os.path.join(gen, "zygwrap.sh"),
            "+0644:e2-vendor-build.prop=%s" % os.path.join(gen, "e2-vendor-build.prop"),
            "+0644:e2-android-env.sh=%s" % os.path.join(gen, "e2-android-env.sh"),
            "+0644:e2-cgroup.rc=%s" % os.path.join(gen, "e2-cgroup.rc"),
            "+0644:e2-cgroup-v1.rc=%s" % os.path.join(gen, "e2-cgroup-v1.rc"),
            "+0644:e2-cgroup-v2.rc=%s" % os.path.join(gen, "e2-cgroup-v2.rc")]
    # 根 /init.rc 也必须走 rc 流水线：真 init 第二阶段会**同时** import /init.rc 与
    # /system/etc/init/hw/init.rc，两份里的 boringssl 自杀闸都要摘，留一份就等于没摘
    # （实测 10:04 那轮串口停在 reboot,boringssl-self-check-failed）。
    # 追加段（claw/ENV）只进 hw 那一份 —— 两份都有的话编排会被跑两遍。
    if not any(x.startswith("init.rc=") for x in rc_args):
        args = args + ["+0644:init.rc=%s" % os.path.join(gen, "init.rc")]
    args = args + rc_args
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
    if a.no_deploy:
        print("  --no-deploy：新镜像留在", OUT)
        print("  私跑：拷到别处用 qemu 单独起一台（换 hostfwd 端口，别撞现镜像）")
        return 0
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
