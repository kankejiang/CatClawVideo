#!/bin/bash
# §6.10 Step5：art-tree 重打测试 initrd + timed 冷启动验证（108 上执行）
# 产物三件套落 art-tree 后：cpio+zstd 重打 → 冷启动计时（桥 ping）→ imageless 消失检测
set -x
cd /root/x86guest
TREE=/root/x86guest/art-tree

# ① 产物完整性（15 分件 + 主件）
n_art=$(ls $TREE/system/framework/arm64/boot*.art 2>/dev/null | wc -l)
n_oat=$(ls $TREE/system/framework/arm64/boot*.oat 2>/dev/null | wc -l)
n_vdex=$(ls $TREE/system/framework/arm64/boot*.vdex 2>/dev/null | wc -l)
echo "产物: art=$n_art oat=$n_oat vdex=$n_vdex"
ls -la $TREE/system/framework/arm64/ | head -8
[ "$n_art" -ge 1 ] || { echo '!! boot.art 缺失（dex2oat 没完成？）'; exit 1; }
echo "（分件数 art=$n_art——单 image 主件模式，locations 校验看启动日志）"

# ② 重打包（zstd -3 快速验证版；定稿分发再换 -19）
rm -f art_initrd_boottest.gz
( cd $TREE && find . | cpio -o -H newc --quiet | zstd -3 -T4 > /root/x86guest/art_initrd_boottest.gz )
ls -la art_initrd_boottest.gz

# ③ timed 冷启动（参数与 restart_merged.sh 完全同构，只换 initrd 与串口日志名）
for p in $(pgrep -f 'qemu-system'); do kill "$p" 2>/dev/null; done
sleep 2
: > /root/x86guest/boottest.log
nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
  -kernel pkg_kernel -initrd art_initrd_boottest.gz \
  -append "console=ttyAMA0 guardport=18602 thunderport=18080 ctrl=11729" \
  -netdev user,id=n0,hostfwd=tcp::18602-:18602 \
  -device virtio-net-pci,netdev=n0 \
  -display none -serial file:/root/x86guest/boottest.log \
  >/dev/null 2>&1 </dev/null &
echo "qemu pid=$!"

# ④ 轮询桥 ping（18602 = guardport，bridge_ping 同款协议）
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
            print('桥就绪 用时 %.1fs' % (time.time() - t0)); sys.exit(0)
    except Exception:
        pass
    time.sleep(2)
print('300s 桥未就绪'); sys.exit(1)
PYEOF
echo "bridge_exit=$?"

# ⑤ imageless 消失检测
echo '=== image 空间报错计数（应为 0）==='
grep -c 'Could not create image space' boottest.log || true
echo '=== bootimg 横幅 ==='
grep -a 'bootimg' boottest.log | head -2
echo done
