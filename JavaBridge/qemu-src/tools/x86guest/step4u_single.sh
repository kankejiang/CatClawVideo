#!/bin/bash
# §6.10 Step4u：单跑形态①并保留全部输出
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework
HOST_BCP="$FW/core-oj.jar:$FW/core-libart.jar"
rm -f /tmp/t1.oat
timeout 40 /usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$HOST_BCP \
  --runtime-arg -Xms64m --android-root=$TREE --instruction-set=arm64 \
  --compiler-filter=verify --image=/system/framework/arm64/boot.art \
  --oat-file=/tmp/t1.oat -j2 > /tmp/t1.out 2>&1
echo "exit=$?"
echo '=== 前 12 行 ==='
head -12 /tmp/t1.out
echo '=== 产物 ==='
ls -la /tmp/t1.oat* 2>/dev/null
