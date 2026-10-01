#!/bin/sh
# T4 对照实验（108 上的 Waydroid = 有完整框架的参照系）。
#
# 为什么去 108：要判断"框架前置由谁提供"，最快的证据不是读 AOSP 源码猜，而是看一台**真的跑着
# framework 的机器**：它的 /apex 是挂载点还是目录？属性区在不在？SELinux 什么模式？init 起了多少服务？
# 这些直接决定路线 (A)/(B)/(C) 的成本估算。
#
# 用法（Windows 侧，脚本自己 scp 过去跑，避开 PowerShell 引号坑）：
#   scp -r tools/framework-probe root@10.0.0.108:/tmp/t4probe
#   ssh root@10.0.0.108 'sh /tmp/t4probe/remote_probe.sh' > docs/research/framework/evidence/108-reference.txt
#
# ⚠ 全程只读：镜像以 `ro,loop` 挂载，结束 umount；不装东西、不改 108 上任何配置。
set -u
MNT=/mnt/t4sysimg
VND=/mnt/t4vendor
PROBE_DIR=$(dirname "$0")
PY=python3

echo "== T4 对照：108（$(hostname)）=="
date -Is
command -v $PY >/dev/null || { echo "108 上没有 python3，探针跑不了"; exit 1; }

mkdir -p "$MNT" "$VND"
IMG=/var/lib/waydroid/images/system.img
VIMG=/var/lib/waydroid/images/vendor.img
umount "$MNT" 2>/dev/null; umount "$VND" 2>/dev/null
mount -o ro,loop "$IMG" "$MNT" 2>/dev/null || { echo "挂载 $IMG 失败"; exit 1; }
mount -o ro,loop "$VIMG" "$VND" 2>/dev/null || echo "(vendor.img 未挂上，只影响 vendor 侧对照)"
echo "挂载点: $MNT  文件系统: $(df -T "$MNT" 2>/dev/null | awk 'NR==2{print $2}')"
# Waydroid 的 system.img 根下还有一层 system/（GSI 布局）⇒ 探针的 --prefix 要按实际布局给，
# 否则路径会变成 system/system/... ，与我们的 initrd 对不齐（2026-10-01 实测踩过）。
if [ -d "$MNT/system" ]; then PRE=""; BASE="$MNT/system"; else PRE="system/"; BASE="$MNT"; fi
echo "镜像布局: 根下有 system/ ⇒ 探针 --prefix '$PRE'"
BP=""
for c in "$BASE/build.prop" "$BASE/etc/build.prop" "$MNT/system/build.prop" "$MNT/build.prop"; do
    [ -f "$c" ] && BP="$c" && break
done
echo "参照系 build.prop: ${BP:-未找到}"

echo
echo "── 0. 这台机器上「框架前置由谁提供」的运行时事实（在 Android 命名空间里问）──"
# getprop/getenforce 在宿主 Debian 上没有；容器里问必须走 `waydroid shell -- sh -c '…'`
# （⚠ 少了 `-- sh -c` 这一层，管道和 `$(…)` 会被 waydroid 自己的 argparse 吃掉，
#   症状是"unrecognized arguments"或整段返回空 —— 2026-10-01 实测踩过）。
SPID=$(pidof system_server 2>/dev/null | awk '{print $1}')
ASH() {
    if command -v waydroid >/dev/null 2>&1; then waydroid shell -- sh -c "$*" 2>&1
    elif [ -n "${SPID:-}" ]; then nsenter -t "$SPID" --mount --pid -- sh -c "$*" 2>&1
    else echo "(既没有 waydroid 也没有 system_server pid，问不了容器)"; fi
}
echo "system_server pid（宿主 pidof）: ${SPID:-未运行}"
echo "getenforce: $(ASH 'getenforce')"
echo "/apex: $(ASH 'mount | grep -c /apex/') 个挂载点，$(ASH 'ls /apex | wc -l') 个条目 ⇒ apexd 真在挂 loop"
ASH 'mount | grep " /apex/" | head -6' | sed 's/^/    /'
echo "/dev/__properties__: $(ASH 'ls /dev/__properties__ | wc -l') 项（属性区的真身，由 init 的 PropertyInit 建）"
ASH 'ls -l /dev/__properties__ | head -4' | sed 's/^/    /'
echo "init.svc.* 计数: $(ASH 'getprop | grep -c init.svc') 条；running: $(ASH "getprop | grep -cE 'init.svc.[a-z].*running'") 条"
echo "关键属性现值:"
ASH 'for p in ro.zygote ro.apex.updatable ro.config.low_ram sys.boot_completed ro.kernel.qemu ro.dalvik.vm.native.bridge persist.sys.dalvik.vm.lib.2 ro.product.cpu.abilist64 apexd.status init.svc.zygote; do printf "    %-34s = %s\n" "$p" "$(getprop $p)"; done'

echo
echo "── 1. 同一套探针跑在参照系上（--prefix 按镜像布局自动对齐，与我们的 initrd 同判据）──"
for s in apex rc props selinux; do
    echo
    if [ "$s" = "props" ]; then EXTRA="--table $BP"; else EXTRA=""; fi
    echo "########## audit_$s.py --source $MNT --prefix $PRE $EXTRA ##########"
    $PY "$PROBE_DIR/audit_$s.py" --source "$MNT" --prefix "$PRE" $EXTRA 2>&1 | sed 's/^/  /'
    echo "########## audit_$s.py 退出码 $? ##########"
done

echo
echo "── 2. vendor 侧（SELinux/vintf 的另一半）──"
for d in "$VND/etc/selinux" "$VND/etc/vintf" "$BASE/etc/vintf" "$BASE/apex"; do
    printf "  %-34s %s\n" "$d" "$(ls "$d" 2>/dev/null | tr '\n' ' ' | cut -c1-200)"
done
echo "  build.prop 里的关键项:"
grep -hE "^(ro.zygote|ro.apex.updatable|ro.config.low_ram|dalvik.vm.heapsize|ro.dalvik.vm.native.bridge)=" \
    "$BASE/system/build.prop" "$BASE/build.prop" 2>/dev/null | sed 's/^/    /'

umount "$MNT" 2>/dev/null; umount "$VND" 2>/dev/null
echo
echo "== 对照结束（已 umount）=="
