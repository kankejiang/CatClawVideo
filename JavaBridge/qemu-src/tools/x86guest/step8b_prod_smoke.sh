#!/bin/bash
# §6.10 Step8b：产品版 initrd 冷启动回归（桥 pong + image 不回退 + 无现场 dex2oat）
cd /root/x86guest
for p in $(pgrep -f qemu-system); do kill "$p" 2>/dev/null; done
sleep 2
: > prod_boot.log
nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
  -kernel pkg_kernel -initrd art_initrd.gz \
  -append "console=ttyAMA0 guardport=18602 thunderport=18080 ctrl=11729" \
  -netdev user,id=n0,hostfwd=tcp::18602-:18602 \
  -device virtio-net-pci,netdev=n0 \
  -display none -serial file:/root/x86guest/prod_boot.log \
  >/dev/null 2>&1 </dev/null &
echo "qemu pid=$!"

python3 - <<'PYEOF'
import socket, time, sys
t0 = time.time()
while time.time() - t0 < 300:
    try:
        s = socket.create_connection(('127.0.0.1', 18602), timeout=2)
        s.sendall(b'{"id":1,"op":"ping"}\n')
        s.settimeout(3)
        r = s.recv(256).decode('utf-8', 'replace')
        s.close()
        if 'pong' in r:
            print('产品版桥就绪 用时 %.1fs' % (time.time() - t0)); sys.exit(0)
    except Exception:
        pass
    time.sleep(2)
print('300s 桥未就绪'); sys.exit(1)
PYEOF

echo '=== image 回退计数（应 0）==='
grep -ac 'Could not create image space' prod_boot.log
echo '=== 现场 dex2oat（应 0）==='
grep -ac 'dex2oat' prod_boot.log
echo '=== bootimg 横幅 ==='
grep -a 'bootimg' prod_boot.log | head -1
echo '=== artlaunch 附加选项（新二进制特征）==='
grep -a '附加 JVM 选项' prod_boot.log | head -3
