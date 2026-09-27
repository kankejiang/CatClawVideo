#!/bin/bash
# 合并 guest（ART 爬虫桥 + 迅雷引擎同 VM）冒烟验证：TCG 起 aarch64，
# 看「桥就绪」与「harness 已起」是否同时出现，以及桥 ping 是否可答。
# 用法（108）：bash run_merged_test.sh [等待秒=200]
cd /root/x86guest || exit 1
WAIT="${1:-200}"
pkill -f qemu-system-aarch64 2>/dev/null; sleep 1
rm -f merged-boot.log
nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
  -kernel pkg_kernel -initrd art_initrd_merged.gz \
  -append "console=ttyAMA0 guardport=18600 thunderport=18080 ctrl=11729" \
  -netdev user,id=n0,hostfwd=tcp::18600-:18600 \
  -device virtio-net-pci,netdev=n0 \
  -display none -serial file:merged-boot.log &
echo "qemu 已起（TCG 慢，等 ${WAIT}s）"
sleep "$WAIT"
echo "=== 关键行 ==="
grep -aE "CATCLAW_DNS|ART guest begin|thunder\]|bridge.GuestMain|等 accept|proppreload|boot natives" merged-boot.log | tail -20
echo "=== 桥 ping 测试 ==="
python3 - <<'PYEOF'
import json, socket
try:
    s = socket.create_connection(("127.0.0.1", 18600), timeout=10)
    s.sendall(b'{"id":1,"op":"ping"}\n')
    s.settimeout(10)
    print("ping 应答:", s.recv(256).decode("utf-8", "replace").strip()[:200])
    s.close()
except Exception as e:
    print("桥不可达:", e)
PYEOF
echo "=== harness 进程 ==="
grep -a "harness" merged-boot.log | tail -3
