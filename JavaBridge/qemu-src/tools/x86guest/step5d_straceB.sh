#!/bin/bash
# §6.10 Step5d：strace + -B 组合，验证 guest mmap(0,1.5GB) 是否透传内核
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework
BCP="$FW/core-oj.jar:$FW/core-libart.jar"
: > /tmp/fakelogd.log
rm -f /tmp/t9.oat
timeout 150 strace -f -e trace=mmap -o /tmp/strace2.log \
  /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=$TREE --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 \
  --image=$FW/arm64/boot.art \
  --oat-file=/tmp/t9.oat \
  --dex-file=$FW/core-oj.jar --dex-location=/system/framework/core-oj.jar \
  --dex-file=$FW/core-libart.jar --dex-location=/system/framework/core-libart.jar \
  -j1 > /tmp/strace2_run.log 2>&1
echo "exit=$?"
echo '=== 1.5GB mmap 是否到达内核 ==='
grep -ac '1610612736' /tmp/strace2.log
grep -a '1610612736' /tmp/strace2.log | head -4
