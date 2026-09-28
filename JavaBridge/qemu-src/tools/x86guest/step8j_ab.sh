#!/bin/bash
# §6.10 Step8j：二分实验——产品版换树版 init（A）/ 换树版 jar（B），各测冷启动
cd /root/x86guest

timeit() {
  local gz=$1 tag=$2
  for p in $(pgrep -f qemu-system); do kill "$p" 2>/dev/null; done
  sleep 2
  : > /root/x86guest/$tag.log
  nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
    -kernel pkg_kernel -initrd "$gz" \
    -append "console=ttyAMA0 guardport=18602 thunderport=18080 ctrl=11729" \
    -netdev user,id=n0,hostfwd=tcp::18602-:18602 \
    -device virtio-net-pci,netdev=n0 \
    -display none -serial file:/root/x86guest/$tag.log \
    >/dev/null 2>&1 </dev/null &
  python3 - "$tag" <<'PYEOF'
import socket, time, sys
tag = sys.argv[1]
t0 = time.time()
while time.time() - t0 < 300:
    try:
        s = socket.create_connection(('127.0.0.1', 18602), timeout=2)
        s.sendall(b'{"id":1,"op":"ping"}\n')
        s.settimeout(3)
        r = s.recv(256).decode('utf-8', 'replace')
        s.close()
        if 'pong' in r:
            print('%s 桥就绪 %.1fs' % (tag, time.time() - t0)); sys.exit(0)
    except Exception:
        pass
    time.sleep(2)
print('%s 300s 未就绪' % tag); sys.exit(1)
PYEOF
}

echo '=== 造包 A（产品 + 树版 init）==='
rm -rf expA && cp -a cmp/prod expA
cp cmp/tree/init expA/init
( cd expA && find . | cpio -o -H newc --quiet 2>/dev/null | zstd -3 -T4 > /root/x86guest/expA.gz )
ls -la expA.gz

echo '=== 造包 B（产品 + 树版 15 jar）==='
rm -rf expB && cp -a cmp/prod expB
cp cmp/tree/system/framework/*.jar expB/system/framework/
( cd expB && find . | cpio -o -H newc --quiet 2>/dev/null | zstd -3 -T4 > /root/x86guest/expB.gz )
ls -la expB.gz

echo
timeit /root/x86guest/expA.gz expA
timeit /root/x86guest/expB.gz expB
