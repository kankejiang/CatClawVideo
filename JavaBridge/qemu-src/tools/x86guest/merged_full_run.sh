#!/bin/bash
# 合并 guest 磁力全链路（108）：杀旧 qemu → 控制端+编排（ctrlserver2.py）→ 起 VM。
# 与冒烟轮的区别：带媒体口 hostfwd（20080）+ 数据面/交换区块设备（store-art.img / swap-art.img），
# 走「TASK MAGNET → 种子 → DL → 媒体流 206 → 块设备直读」完整链路。
# 用法: bash merged_full_run.sh [磁力URI] [名字]
cd /root/x86guest || exit 1
MAG="${1:-magnet:?xt=urn:btih:1f8e2b67e3142cd3b0b26128c6d2ffb8c33c00db}"
NAME="${2:-merged-smoke.mp4}"
for p in $(pgrep -f 'qemu-system'); do kill "$p" 2>/dev/null; done
for p in $(pgrep -f 'ctrlserver2'); do kill "$p" 2>/dev/null; done
sleep 2
# 数据面/交换区（稀疏镜像：写多少占多少）
[ -f store-art.img ] || truncate -s 64G store-art.img
[ -f swap-art.img ] || truncate -s 6G swap-art.img
rm -f ctrl_out.log ctrl_report.log merged-boot.log
# 控制端 + 编排：18080 控制口，20080 媒体口（宿主 hostfwd → guest 代理）
nohup python3 ctrlserver2.py "TASK MAGNET $MAG $NAME" 18080 20080 > ctrl_out.log 2>&1 &
sleep 1
nohup qemu-system-aarch64 -M virt -cpu cortex-a57 -m 3072 -smp 2 \
  -kernel pkg_kernel -initrd art_initrd_merged.gz \
  -append "console=ttyAMA0 guardport=18602 thunderport=18080 ctrl=11729 blkdev=/dev/vda swapdev=/dev/vdb" \
  -drive file=store-art.img,if=none,id=hub0,format=raw,cache=unsafe \
  -device virtio-blk-pci,drive=hub0 \
  -drive file=swap-art.img,if=none,id=swp0,format=raw,cache=unsafe \
  -device virtio-blk-pci,drive=swp0 \
  -netdev user,id=n0,hostfwd=tcp::18602-:18602,hostfwd=tcp::20080-:20080 \
  -device virtio-net-pci,netdev=n0 -display none -serial file:merged-boot.log \
  >/dev/null 2>&1 </dev/null &
echo "STARTED qemu pid=$!"
