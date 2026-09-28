#!/bin/bash
# §6.10 Step4h：--dex-file 直接指 jar（含真 dex）重试；前台 timeout 240s 抓完整报错
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework

BCP="/system/framework/core-oj.jar:/system/framework/core-libart.jar:/system/framework/conscrypt.jar:/system/framework/okhttp.jar:/system/framework/bouncycastle.jar:/system/framework/apache-xml.jar:/system/framework/ext.jar:/system/framework/framework.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/framework-oahl-backward-compatibility.jar:/system/framework/android.test.base.jar"

mkdir -p /dev/socket
pgrep -f fakelogd >/dev/null || /usr/bin/qemu-aarch64-static -L $TREE ./art-tree/fakelogd > /tmp/fakelogd.log 2>&1 &
sleep 1

rm -f $FW/arm64/boot.oat $FW/arm64/boot.vdex $FW/arm64/boot.art

timeout 240 /usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms64m --runtime-arg -Xmx1536m \
  --android-root=$TREE \
  --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify \
  --base=0x70000000 \
  --image=/system/framework/arm64/boot.art \
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
  -j4 > /root/x86guest/dex2oat_probe2.log 2>&1
echo "exit=$?"
echo '=== stderr 头 20 行 ==='
head -20 /root/x86guest/dex2oat_probe2.log
echo '=== 产物 ==='
ls -la $FW/arm64/boot* 2>/dev/null | head -6
