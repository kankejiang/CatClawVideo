#!/bin/sh
# T4 探针 6｜guest **运行时**只读盘点（经 adb 隧道，不改任何东西）。
#
# 为什么单独要一支：镜像里"有没有"和跑起来"看不看得到"是两件事 ——
# 属性区 /dev/__properties__、init.svc.*、SELinux 模式、PID1 真身，都只有运行时才作数。
#
# 用法（Windows / Git Bash）：
#   sh tools/framework-probe/probe_guest_runtime.sh
# 前提：应用带 CATCLAW_BRIDGE_DEBUG 起过、guest 已起（home-debug.log 里有 `adb 隧道已开：127.0.0.1:<port>`）。
set -u
ADB="${ADB:-$HOME/AppData/Local/Android/Sdk/platform-tools/adb.exe}"
LOG="${LOG:-$APPDATA/CatClawVideo.debug/home-debug.log}"
[ -f "$LOG" ] || { echo "找不到日志 $LOG（用 LOG=... 指定）"; exit 1; }
[ -x "$ADB" ] || { echo "找不到 adb（用 ADB=... 指定）"; exit 1; }

PORT=$(grep -aoE "adb 隧道已开：adb connect 127.0.0.1:[0-9]+" "$LOG" | tail -1 | grep -oE "[0-9]+$")
[ -n "$PORT" ] || { echo "日志里没有 adb 隧道端口 —— guest 起过吗？"; exit 1; }
DEV="127.0.0.1:$PORT"
"$ADB" connect "$DEV" >/dev/null 2>&1
echo "== probe_guest_runtime（设备 $DEV）=="
echo "时间: $(date -Is)"
# ⚠ 每条问句单独一次 shell 调用：proppreload 会在**每个新进程**里打一行接管日志，
# 一次调用里问多件事会把输出搅在一起 —— 而那行日志本身就是"表是 per-process 的"这条证据。
q() { printf "  %-46s %s\n" "$1" "$("$ADB" -s "$DEV" shell "$2" 2>&1 | grep -av proppreload | tr -d '\r' | tr '\n' ' ')"; }
q "/dev/__properties__ 是否存在" 'ls -d /dev/__properties__ 2>&1'
q "属性区条目数" 'ls /dev/__properties__ 2>/dev/null | wc -l'
q "init.svc.* 条数（服务管理状态）" 'getprop | grep -c init.svc'
q "sys.boot_completed" 'getprop sys.boot_completed'
q "getenforce（SELinux 模式）" 'getenforce'
q "PID1 真身" 'cat /proc/1/cmdline | tr "\000" " "'
q "/apex 运行时内容" 'ls /apex | tr "\n" " "'
q "关键二进制在位" 'ls /system/bin | grep -E "^(init|apexd|servicemanager|hwservicemanager|ueventd|app_process64|secilc)$" | tr "\n" " "'
q "init 二进制（镜像里有 2.28MB，运行时在不在是两件事）" 'ls -l /system/bin/init 2>&1'
q "/system 是怎么来的" 'mount | grep -a " /system" | head -2'
q "/system/bin 条目数" 'ls /system/bin | wc -l'
q "binder 设备" 'ls -l /dev/binder* /dev/hwbinder* /dev/vndbinder* 2>/dev/null | wc -l'
echo '  （旁证：上面每条问句都会先打出 [proppreload] 接管 Android 属性 API：161 条 —— '
echo '    每个新进程重打一次，说明那张表是**进程内**的，不是全机共享的属性服务。）'
echo
echo "判读:"
echo "  · 属性区 0 项 + init.svc 0 条 + PID1 是 busybox sh ⇒ 我们**没有** init 的属性服务与服务管理，"
echo "    框架前置里最硬的一块就是这个；镜像里 init 二进制与 rc 都在（见 audit_rc/audit_selinux）。"
echo "  · getenforce 已是 Disabled ⇒ '缺 SELinux 策略'不成立为阻塞点（与 108 参照系一致）。"
