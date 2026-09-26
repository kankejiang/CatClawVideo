#!/bin/bash
# x86 guest 首启测试：后台起 QEMU(KVM) → 等 ART → 探桥握手 → 抓串口日志
cd /root/x86guest || exit 1
pkill -f qemu-system-x86_64 2>/dev/null; sleep 1
nohup qemu-system-x86_64 -accel kvm -M q35 -m 2048 -smp 4 \
  -kernel linux-data/boot/vmlinuz-6.1.0-50-amd64 \
  -initrd art_initrd_x64.gz \
  -append "console=ttyS0 guardport=18600" \
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:18600-:18600 \
  -device virtio-net-pci,netdev=n0 \
  -nographic -no-reboot > qemu-boot.log 2>&1 &
echo "qemu pid=$!"
sleep 45
echo "==== 桥握手测试（op=ping）===="
printf '{"id":1,"op":"ping"}\n' | timeout 6 nc -w 4 127.0.0.1 18600 || echo "(握手未通)"
echo "==== 串口日志（前 40 行）===="
head -40 qemu-boot.log
echo "==== 串口日志（后 40 行）===="
tail -40 qemu-boot.log
