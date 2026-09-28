#!/bin/bash
# §6.10 Step5c：strace 抓 qemu-user 的 host mmap 真实行为（LOS 失败那一刻）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework
which strace || apt-get install -y strace 2>&1 | tail -1

BCP="$FW/core-oj.jar:$FW/core-libart.jar"
rm -f /tmp/t9.oat
timeout 150 strace -f -e trace=mmap -o /tmp/strace.log \
  /usr/bin/qemu-aarch64-static -R 0x800000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=$TREE --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 \
  --image=$FW/arm64/boot.art \
  --oat-file=/tmp/t9.oat \
  --image-classes=$TREE/system/etc/preloaded-classes \
  --dex-file=$FW/core-oj.jar --dex-location=/system/framework/core-oj.jar \
  --dex-file=$FW/core-libart.jar --dex-location=/system/framework/core-libart.jar \
  -j1 > /tmp/strace_run.log 2>&1
echo "exit=$?"
echo '=== 1.5GB (1610612736) 的 mmap 调用 ==='
grep -a '1610612736' /tmp/strace.log | head -6
echo '=== ENOMEM 的 mmap ==='
grep -ac 'ENOMEM' /tmp/strace.log
grep -a 'ENOMEM' /tmp/strace.log | tail -4
