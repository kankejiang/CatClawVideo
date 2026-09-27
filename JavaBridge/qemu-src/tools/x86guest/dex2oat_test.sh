#!/bin/bash
# §6.10 Step3 v2：fakelogd 捕获 dex2oat 真实报错 + --boot-image（非 boot 编译必需）
cd /root/x86guest
TREE=/root/x86guest/art-tree
BCP="/system/framework/core-oj.jar:/system/framework/core-libart.jar:/system/framework/conscrypt.jar:/system/framework/okhttp.jar:/system/framework/bouncycastle.jar:/system/framework/apache-xml.jar:/system/framework/ext.jar:/system/framework/framework.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/framework-oahl-backward-compatibility.jar:/system/framework/android.test.base.jar"

# fakelogd（aarch64）监听宿主 /dev/socket/logdw —— dex2oat（qemu-user）写同一路径，日志落地
mkdir -p /dev/socket
pgrep -f fakelogd >/dev/null || /usr/bin/qemu-aarch64-static -L $TREE ./art-tree/fakelogd > /tmp/fakelogd.log 2>&1 &
sleep 1

mkdir -p oat-out; rm -f oat-out/test-ext.odex
/usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms64m --runtime-arg -Xmx512m \
  --boot-image=$TREE/system/framework/boot.art \
  --android-root=$TREE \
  --instruction-set=arm64 --instruction-set-features=default \
  --dex-file=$TREE/system/framework/ext.jar \
  --dex-location=/system/framework/ext.jar \
  --oat-file=/root/x86guest/oat-out/test-ext.odex \
  -j4 2>&1 | tail -8

echo "=== 产物 ==="; ls -la oat-out/ 2>/dev/null
echo "=== fakelogd 尾部（dex2oat 的真实报错）==="; tail -20 /tmp/fakelogd.log 2>/dev/null
