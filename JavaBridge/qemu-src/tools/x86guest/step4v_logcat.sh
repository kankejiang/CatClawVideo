#!/bin/bash
# §6.10 Step4v：fakelogd 在手重跑形态①，抓 files/locations 实数
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework
HOST_BCP="$FW/core-oj.jar:$FW/core-libart.jar"

mkdir -p /dev/socket
pgrep -f fakelogd >/dev/null || /usr/bin/qemu-aarch64-static -L $TREE ./art-tree/fakelogd > /tmp/fakelogd.log 2>&1 &
sleep 1
: > /tmp/fakelogd.log

rm -f /tmp/t1.oat
timeout 40 /usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$HOST_BCP \
  --runtime-arg -Xms64m --android-root=$TREE --instruction-set=arm64 \
  --compiler-filter=verify --image=/system/framework/arm64/boot.art \
  --oat-file=/tmp/t1.oat -j2 > /tmp/t1.out 2>&1
echo "exit=$?"

echo '=== fakelogd 关键行 ==='
grep -a 'match\|files\|locations\|setting boot' /tmp/fakelogd.log | grep -av '^\[logd\] dex2oatd$' | head -8
