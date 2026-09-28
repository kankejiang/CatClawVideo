#!/bin/bash
# §6.10 Step5M：全量编译（guest 形态路径：android-root=/ + /system/… 全部路径）
# 依据：15-dex 旧轮用「宿主路径 + android-root=art-tree」→ boot.oat 记录了坏 image location
#       （/system/framework//root/...）；本轮换成运行时同款 guest 视角路径（/system 软链到 art-tree）
cd /root/x86guest
TREE=/root/x86guest/art-tree
: > /tmp/fakelogd.log
[ -e /system ] || ln -s $TREE/system /system

FW=/system/framework
BCP="$FW/core-oj.jar:$FW/core-libart.jar:$FW/conscrypt.jar:$FW/okhttp.jar:$FW/bouncycastle.jar:$FW/apache-xml.jar:$FW/ext.jar:$FW/framework.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/framework-oahl-backward-compatibility.jar:$FW/android.test.base.jar"

rm -f $FW/arm64/boot*.art $FW/arm64/boot*.oat $FW/arm64/boot*.vdex

cd /root/x86guest
nohup strace -f -e trace=mmap -o /tmp/strace4.log \
  /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=/ --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 --multi-image \
  --image=$FW/arm64/boot.art \
  --oat-file=$FW/arm64/boot.oat \
  --image-classes=/system/etc/preloaded-classes \
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
  -j1 > /root/x86guest/dex2oat_B.log 2>&1 &

echo "dex2oat(B 全量) PID=$!  日志 /root/x86guest/dex2oat_B.log"
sleep 30
ps -eo pid,etime,args --no-headers | grep dex2oatd | grep -v grep | head -1
