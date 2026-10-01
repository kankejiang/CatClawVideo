#!/bin/bash
# T3 一键脚本：Waydroid 坑修复 → 会话拉起（含 headless Weston）→ serve.py
#
# 用法：
#   bash serve.sh                            # 前台跑（默认 --port 27183 --codec h264 --fps 30）
#   bash serve.sh --port 27183 --codec h264 --fps 30
#   bash serve.sh --codec jpeg               # 兜底：screencap-JPEG
#   bash serve.sh -d                         # 后台跑（日志 /tmp/catclaw-stream.log）
#
# 坑对照（docs/tasks/T3-Waydroid流服务.md 第五节 + 本次实测）：
#   坑③ 缺 /run/user/0/pulse/native → touch 补上（否则挂载报 Failed to setup mount entries）
#   坑① 只 session start 不够，必须 show-full-ui，否则容器被 lxc-freeze
#   坑② session stop 会删 waydroid0 网桥 → 本脚本只 restart container，绝不 session stop
#   实测新坑：session 必须跑在 Wayland 合成器里。108 上是 b11 会话部署的 headless
#   weston（wl-headless socket）——不在就自动拉一个；SSH 环境必须显式指 WAYLAND_DISPLAY。
#   实测新坑：Android 灭屏后 screenrecord 零输出 → 唤醒 + svc power stayon true。
set -e
cd "$(dirname "$0")"

DAEMON=0
pass=()
while [ $# -gt 0 ]; do
  case "$1" in
    -d|--daemon) DAEMON=1; shift;;
    *) pass+=("$1"); shift;;
  esac
done

echo "[serve] ① 坑修复：pulse native"
mkdir -p /run/user/0/pulse && touch /run/user/0/pulse/native

echo "[serve] ② headless Weston（waydroid session 的 Wayland 合成器）"
if ! pgrep -f "weston --backend=headless" >/dev/null; then
  nohup weston --backend=headless --socket=wl-headless --width=1280 --height=720 \
      >/dev/null 2>&1 &
  sleep 3
fi
export WAYLAND_DISPLAY=wl-headless
export XDG_RUNTIME_DIR=/run/user/0

echo "[serve] ③ container 状态"
if ! systemctl is-active --quiet waydroid-container; then
  echo "[serve]    重启 waydroid-container"
  systemctl restart waydroid-container
  sleep 4
fi

echo "[serve] ④ session start + show-full-ui"
waydroid session start >/dev/null 2>&1 || true
sleep 5
waydroid show-full-ui >/dev/null 2>&1 || true
sleep 2
waydroid status 2>&1 | head -2 || true

echo "[serve] ⑤ 等 adb 设备 + 唤醒常亮"
DEV=""
for i in $(seq 1 30); do
  adb connect 192.168.240.112:5555 >/dev/null 2>&1
  DEV=$(adb devices 2>/dev/null | awk '/device$/{print $1}' | head -1)
  [ -n "$DEV" ] && break
  sleep 2
done
[ -n "$DEV" ] || { echo "[serve] 无 adb 设备，放弃"; exit 1; }
adb -s "$DEV" shell input keyevent 224 >/dev/null 2>&1 || true          # WAKEUP
adb -s "$DEV" shell svc power stayon true >/dev/null 2>&1 || true       # 灭屏会让 screenrecord 零输出
echo "[serve]    设备=$DEV"

if [ "$DAEMON" = "1" ]; then
  nohup python3 serve.py "${pass[@]}" >> /tmp/catclaw-stream.log 2>&1 &
  echo "[serve] 已后台启动 pid=$!（日志 tail -f /tmp/catclaw-stream.log）"
else
  exec python3 serve.py "${pass[@]}"
fi
