#!/bin/bash
# 合并 guest 一键重启 + 验证（108 上执行）：
# 杀全部 qemu → 后台起 18080 监听（赶在 harness 之前）→ 起合并 guest。
cd /root/x86guest || exit 1
for p in $(pgrep -f 'qemu-system'); do kill "$p" 2>/dev/null; done
sleep 2
nohup python3 verify_merged.py > verify_out.log 2>&1 &
sleep 1
nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
  -kernel pkg_kernel -initrd art_initrd_merged.gz \
  -append "console=ttyAMA0 guardport=18602 thunderport=18080 ctrl=11729" \
  -netdev user,id=n0,hostfwd=tcp::18602-:18602 \
  -device virtio-net-pci,netdev=n0 \
  -display none -serial file:/root/x86guest/merged-boot.log \
  >/dev/null 2>&1 </dev/null &
echo "STARTED pid=$!"
