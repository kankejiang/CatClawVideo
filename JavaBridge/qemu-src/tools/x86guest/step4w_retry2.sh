#!/bin/bash
# §6.10 Step4w：系统构建形态重试（BCP 宿主路径 / --image 宿主路径 / location 用 /system 编码）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework

mkdir -p /dev/socket
pgrep -f fakelogd >/dev/null || /usr/bin/qemu-aarch64-static -L $TREE ./art-tree/fakelogd > /tmp/fakelogd.log 2>&1 &
sleep 1

BCP="$FW/core-oj.jar:$FW/core-libart.jar:$FW/conscrypt.jar:$FW/okhttp.jar:$FW/bouncycastle.jar:$FW/apache-xml.jar:$FW/ext.jar:$FW/framework.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/framework-oahl-backward-compatibility.jar:$FW/android.test.base.jar"

rm -f $FW/arm64/boot.oat $FW/arm64/boot.vdex $FW/arm64/boot.art

# ⚠ -j1：qemu-user 下 dex2oat 的多编译线程 attach runtime 会 SIGABRT（futex/clone 语义）
# ⚠ -R 1TB：guest VA 预留窗口放开——ART 的 LOS mmap(0, 1.5GB) 在 qemu 默认窗口找不到
#   连续空洞（large_object_space.cc Check failed → SIGABRT）；env QEMU_RESERVED_VA=0 被当
#   falsy 落回默认，改用命令行 -R 显式指定
# ⚠ -B 0x4000000000（guest_base=256GB）：guest mmap(0) 直接透传 host 内核挑地址，
#   绕开 qemu 空洞搜索（其 mmap_next_start 游标不回落 → ART 预留后 LOS 1.5GB 永远找不到洞）
# ⚠ -R 4TB：guest 窗口；guest_base(256GB)+4TB ≤ host 用户 VA 128TB，预留可落地
nohup /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx128m \
  --runtime-arg -XX:HeapGrowthLimit=128m --runtime-arg -XX:HeapStartSize=32m \
  --android-root=$TREE \
  --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify \
  --base=0x70000000 \
  --image=$FW/arm64/boot.art \
  --oat-file=$FW/arm64/boot.oat \
  --image-classes=$TREE/system/etc/preloaded-classes \
  --dex-file=$FW/core-oj.jar --dex-location=/system/framework/core-oj.jar \
  --dex-file=$FW/core-libart.jar --dex-location=/system/framework/core-libart.jar \
  --dex-file=$FW/conscrypt.jar --dex-location=/system/framework/conscrypt.jar \
  --dex-file=$FW/okhttp.jar --dex-location=/system/framework/okhttp.jar \
  --dex-file=$FW/bouncycastle.jar --dex-location=/system/framework/bouncycastle.jar \
  --dex-file=$FW/apache-xml.jar --dex-location=/system/framework/apache-xml.jar \
  --dex-file=$FW/ext.jar --dex-location=/system/framework/ext.jar \
  --dex-file=$FW/framework.jar --dex-location=/system/framework/framework.jar \
  --dex-file=$FW/telephony-common.jar --dex-location=/system/framework/telephony-common.jar \
  --dex-file=$FW/voip-common.jar --dex-location=/system/framework/voip-common.jar \
  --dex-file=$FW/ims-common.jar --dex-location=/system/framework/ims-common.jar \
  --dex-file=$FW/android.hidl.base-V1.0-java.jar --dex-location=/system/framework/android.hidl.base-V1.0-java.jar \
  --dex-file=$FW/android.hidl.manager-V1.0-java.jar --dex-location=/system/framework/android.hidl.manager-V1.0-java.jar \
  --dex-file=$FW/framework-oahl-backward-compatibility.jar --dex-location=/system/framework/framework-oahl-backward-compatibility.jar \
  --dex-file=$FW/android.test.base.jar --dex-location=/system/framework/android.test.base.jar \
  -j1 > /root/x86guest/dex2oat_w.log 2>&1 &

echo "dex2oat PID=$!（后台，-j1 单线程）"
sleep 30
ps -eo pid,etime,pcpu,args --no-headers | grep 'aarch64-static -L.*dex2oatd' | grep -v grep | head -1
