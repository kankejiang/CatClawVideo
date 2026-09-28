#!/bin/busybox sh
# ART guest 的 /init —— QEMU(TCG) 里把「真 Android ART + 桥」拉起来。
#
# 与宿主的关系：桥(bridge.GuestMain)在本 guest 内监听端口，宿主经 slirp hostfwd 用
# **与桌面 JRE 完全相同的行协议**说活（见 CatClawVideo.Core/Providers/JavaSpiderRuntime.cs）。
# 端口由宿主经 kernel cmdline 的 `guardport=` 告诉这里（复用 QemuHostRuntime 现成的那条参数，
# 与迅雷/Guard VM 的口令参数同一风格）—— **绝不能烧死在 initrd 里**，否则多实例会撞号。
#
# 为什么每步都在（都是 2026-09-25 一条条撞出来的，少一步就静默失败）：
#   · /dev 必须是 tmpfs + 手 mknod：没有 init/udev，ART 要 /dev/null /dev/urandom；
#   · insmod 顺序 virtio_mmio → virtio_blk → virtio_net：顺序错就看不到网卡/盘；
#   · fakelogd：没有 logd 时 liblog 整条静默丢弃，ART/linker 的真话全看不见；
#   · LD_PRELOAD=proppreload.so：本 guest 没有 Android init，/dev/__properties__ 不存在，
#     属性 API + ashmem 全由它接管；顺带 RegisterNatives 补 boot classpath 那批
#     native（SystemProperties/Log/MessageQueue/SystemClock/ThreadPool —— 真机上归
#     libandroid_runtime，在 zygote 里注册，我们没有 zygote）；
#   · ANDROID_* 与 /data/dalvik-cache/arm64：少一个 ART 直接 abort。
BB=/bin/busybox
# /system/bin 必须在 PATH 里：壳 jar 会直接 exec `chmod`（给解出来的 config.db 改权限），
# 缺它时是 `Cannot run program "chmod": error=2`（2026-09-26 装机台架实测）。
export PATH=/system/bin:/bin

getarg() {  # 从 /proc/cmdline 取 key=value
    $BB sed -n 's/.*'"$1"'=\([^ ]*\).*/\1/p' /proc/cmdline 2>/dev/null | $BB head -1
}

$BB mkdir -p /proc /sys /dev /tmp /data /data/catclaw
$BB mount -t proc proc /proc 2>/dev/null
$BB mount -t sysfs sys /sys 2>/dev/null
# /dev 优先用 devtmpfs：Android 内核（kernel-ranchu）自带 CONFIG_DEVTMPFS，挂上就有
# null/zero/ashmem/binder 这类设备节点；换 Alpine 那种 generic 内核时没有 devtmpfs，
# 才退回 tmpfs + 手动 mknod。顺序反了会让后面的 `2>/dev/null` 全部静默失败
# （/dev/null 不存在时 busybox 直接掐掉那条命令）—— 2026-09-26 换内核时踩到。
$BB mount -t devtmpfs devtmpfs /dev 2>/dev/null || $BB mount -t tmpfs tmpfs /dev -o mode=0755 2>/dev/null
for nd in null:c,1,3 zero:c,1,5 random:c,1,8 urandom:c,1,9 tty0:c,4,0; do
    set -- $nd; $BB mknod /dev/$1 $2 $3 $4 2>/dev/null
done
for m in virtio_mmio virtio_blk failover net_failover virtio_net; do
    $BB insmod /modules/$m.ko 2>/dev/null
done

PORT=$(getarg guardport)
[ -n "$PORT" ] || PORT=18581
# 域名服务器交给宿主转发：本 guest 没有 netd，bionic 拿不到任何服务器；
# 而 slirp 自带的 10.0.2.3:53 实测回 ICMP port-unreachable。宿主在回环上起一个
# TCP 两行文本的小转发（端口经 cmdline 的 ctrl= 传来），proppreload 的 dnsshim 用它。
DP=$(getarg ctrl)
if [ -n "$DP" ]; then export CATCLAW_DNS=10.0.2.2:$DP; echo "[dns] 走宿主转发 $CATCLAW_DNS"; fi
echo "=== CatClaw ART guest begin (bridgeport=$PORT) ==="

# 一次性闸门测量（binder/ashmem/devtmpfs）已经做完，结论记在
# qemu-arm-boundary 的知识条目里；这里不再留调试块 —— 它插在 DNS 诊断之前，
s 内不再放调试输出。

# 回环必须自己拉起来：裸内核 + initrd 里 lo 是 administratively down 的，
# 于是任何 bind/connect 到 127.0.0.1 都直接 EADDRNOTAVAIL（Cannot assign requested address）。
# 症状（2026-09-26 实测）：jar 的 proxy 自回调调不到（bridge 侧 bind 失败），
# 解密壳自己从 9978 开始逐个「端口检测失败」扫到 9992+，detailContent 返回 0 字节。
$BB ifconfig lo 127.0.0.1 netmask 255.0.0.0 up 2>&1
$BB ifconfig eth0 10.0.2.15 netmask 255.255.255.0 up 2>&1
$BB route add default gw 10.0.2.2 2>&1
# DNS 必须自己写：slirp 的 DNS 在 10.0.2.3，而本 guest 没有 init/systemd 去生成 resolv.conf。
# 缺它的症状很隐蔽也很贵（2026-09-25 实测）：okhttp 报 "Unable to resolve host"，
# homeContent 静默返回空 class/list，searchContent 一路重试到 90830ms 才回空。
$BB mkdir -p /etc
echo "nameserver 10.0.2.3" > /etc/resolv.conf
echo "search lan" >> /etc/resolv.conf
# --- DNS 诊断（dnsdbg=1 时才跑；2026-09-25 实测：bionic 不读 /etc/resolv.conf，
#     busybox/musl 那条能通到 slirp 的 10.0.2.3，所以要确认的是「带属性垫片的 bionic 能不能解析」）---
if [ -n "$(getarg dnsdbg)" ]; then
    echo "[dns] /etc/resolv.conf: $($BB cat /etc/resolv.conf | $BB tr '\n' ' ')"
    echo "[dns] 无垫片 getprop net.dns1 = [$(/system/bin/getprop net.dns1 2>&1)]"
    echo "[dns] 带垫片 getprop net.dns1 = [$(LD_PRELOAD=/proppreload.so /system/bin/getprop net.dns1 2>&1)]"
    LD_PRELOAD=/proppreload.so DNSHIM=1 /system/bin/ping -c 1 -w 3 www.baidu.com 2>&1 | $BB head -2 | $BB sed 's/^/[dns 垫片bionic] /'
    /system/bin/ping -c 1 -w 3 www.baidu.com 2>&1 | $BB head -2 | $BB sed 's/^/[dns 裸bionic]    /'
    $BB nslookup www.baidu.com 10.0.2.3 2>&1 | $BB head -3 | $BB sed 's/^/[dns bb] /'
fi

/fakelogd &
FL=$!
$BB sleep 1

export ANDROID_ROOT=/system ANDROID_DATA=/data ANDROID_STORAGE=/storage
export ANDROID_ART_ROOT=/system ANDROID_TEMPLATES_ROOT=/system/template/ TMPDIR=/data/local/tmp
export LD_LIBRARY_PATH=/system/lib64:/system/bin
$BB mkdir -p /data/local/tmp /data/dalvik-cache/arm64 /data/misc /data/system /data/catclaw

# ★ boot 镜像原位启用（2026-09-27）：镜像里的 boot.oat 以 --multi-image -Xnorelocate 编译
#   （artlaunch.c 的 BCP 注释），可**原位使用**——不设 CATCLAW_BCP_LOCATIONS 时 ART 会尝试
#   把 boot.art 重定位到 /data/dalvik-cache（需要 zygote 权限语义）→ 失败回退 imageless
#   running，每次启动全量 JIT（桥冷启动 36s 的主因，实测 22:37 轮）。
#   locations 与 BCP 15 项一一对应（core-oj 对应主镜像 boot.art，其余 boot-<名>.art）。
if [ -f /system/framework/arm64/boot.art ]; then
    export CATCLAW_BCP_LOCATIONS="/system/framework/arm64/boot.art:/system/framework/arm64/boot-core-libart.art:/system/framework/arm64/boot-conscrypt.art:/system/framework/arm64/boot-okhttp.art:/system/framework/arm64/boot-bouncycastle.art:/system/framework/arm64/boot-apache-xml.art:/system/framework/arm64/boot-ext.art:/system/framework/arm64/boot-framework.art:/system/framework/arm64/boot-telephony-common.art:/system/framework/arm64/boot-voip-common.art:/system/framework/arm64/boot-ims-common.art:/system/framework/arm64/boot-android.hidl.base-V1.0-java.art:/system/framework/arm64/boot-android.hidl.manager-V1.0-java.art:/system/framework/arm64/boot-framework-oahl-backward-compatibility.art:/system/framework/arm64/boot-android.test.base.art"
    # ★ -Xnorelocate 必须同传：不传时 ART 仍要把 boot.art 重定位到 /data/dalvik-cache
    #   （zygote 语义，我们没有）→ 重定位失败回退 imageless，镜像等于白启用。
    #   镜像本就以 -Xnorelocate 编译（见 artlaunch.c BCP 注释），原位 mmap 即可。
    # ⚠ 现状（2026-09-27 三轮实测：locations / +norelocate / +zygote）：校验仍失败回退
    #   imageless（45~46s 不变）。根因 = boot.oat 的 dex checksum 与 initrd 里 framework
    #   jar 链布局/历史裁剪不对齐。正解 = 离线 dex2oat 用**当前 jar**自产配套 boot 镜像
    #   （qemu-aarch64-static 可跑 dex2oat，108 已验证 user-mode 路线）——提速与轻量化
    #   一并解决（自产镜像只含保留的 jar），单独立项。
    export CATCLAW_JVM_EXTRA="-Xnorelocate${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"
    echo "[bootimg] CATCLAW_BCP_LOCATIONS + -Xnorelocate 已导出（boot 镜像原位启用）"
fi

# 桥的 classpath：gb.dex（桥，已剔除 TVBox/壳 jar 自己定义的类，避免 parent-first 抢位）
#              + tvbox.apk（真 TVBox 代码：crawler/okhttp/gson… 与解析侧 arm64 库同源）
# ART 的 boot classpath 由 artlaunch 内部按 boot.oat 的原序给出（15 个 jar），不在这里传。
LD_PRELOAD=/proppreload.so /artlaunch bridge.GuestMain /gb.dex:/tvbox.apk $PORT &
LP=$!

# 桥是常驻服务：跟着宿主 `{"op":"exit"}` 走；这里是宿主没来的兜底（防孤儿 VM）
i=0
while [ $i -lt 10800 ]; do
    $BB sleep 2; i=$((i+1))
    $BB kill -0 $LP 2>/dev/null || break
done
$BB kill $LP $FL 2>/dev/null
echo "=== CatClaw ART guest end ==="
