#!/bin/bash
# §6.10 Step5O：探针 D——--boot-image 指定记录用 location（--image 仅写盘；不带 --image-classes）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=/system/framework
[ -e /system ] || ln -s $TREE/system /system
mkdir -p /dev/socket
: > /tmp/fakelogd.log

rm -f $FW/arm64/boot.art $FW/arm64/boot.oat $FW/arm64/boot.vdex $FW/arm64/boot-core-libart.art $FW/arm64/boot-core-libart.oat $FW/arm64/boot-core-libart.vdex

timeout 150 /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$FW/core-oj.jar:$FW/core-libart.jar \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=/ --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 --multi-image \
  --boot-image=$FW/arm64/boot.art \
  --image=$FW/arm64/boot.art \
  --oat-file=$FW/arm64/boot.oat \
  --dex-file=$FW/core-oj.jar --dex-location=/system/framework/core-oj.jar \
  --dex-file=$FW/core-libart.jar --dex-location=/system/framework/core-libart.jar \
  -j1 > /root/x86guest/probeD.log 2>&1
echo "exit=$?"
echo '=== 产物 ==='
ls -la $TREE/system/framework/arm64/boot.art $TREE/system/framework/arm64/boot-core-libart.art 2>/dev/null | head -3
echo '=== image location 记录（期望 /system/framework/arm64/boot.art:... 无双前缀）==='
strings -n 20 $FW/arm64/boot.oat 2>/dev/null | grep -a '\.art' | grep -av dex2oatd | head -2
