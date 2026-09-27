#!/bin/bash
# 计时版合并 guest 重启：QEMU 起 → 桥 ping 首次 pong 的墙钟差（对比 boot 镜像优化前后）。
cd /root/x86guest || exit 1
date +%s > /tmp/t0
bash restart_merged.sh
for i in $(seq 1 150); do
  sleep 2
  if python3 bridge_ping.py 2>/dev/null | grep -q pong; then
    t1=$(date +%s); t0=$(cat /tmp/t0)
    echo "BRIDGE_READY=$((t1 - t0))s（QEMU 起 → 桥 ping pong）"
    exit 0
  fi
done
echo "TIMEOUT（300s 内桥未就绪）"
