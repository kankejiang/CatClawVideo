#!/bin/bash
# §6.10 Step7a：预编 gb.dex 的 quicken oat（dalvik-cache 预置方案第一步）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=/system/framework
[ -e /system ] || ln -s $TREE/system /system
mkdir -p /dev/socket
mkdir -p /root/x86guest/dalvik-preinit/arm64
OUT=/root/x86guest/dalvik-preinit/arm64
: > /tmp/fakelogd.log

echo '=== dex2oatd 的输出参数确认 ==='
strings -n 8 ./dex2oatd | grep -aE '^--output-vdex|^--oat-file|^--boot-image|^--output' | head -8

BCP="$FW/core-oj.jar:$FW/core-libart.jar:$FW/conscrypt.jar:$FW/okhttp.jar:$FW/bouncycastle.jar:$FW/apache-xml.jar:$FW/ext.jar:$FW/framework.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/framework-oahl-backward-compatibility.jar:$FW/android.test.base.jar"

echo
echo '=== 试编 gb.dex（quicken）==='
rm -f $OUT/gb.dex $OUT/gb.vdex
timeout 400 /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=/ --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=quicken \
  --boot-image=/system/framework/boot.art \
  --dex-file=/gb.dex \
  --oat-file=$OUT/gb.dex \
  --output-vdex=$OUT/gb.vdex \
  -j1 > /root/x86guest/preoat_gb.log 2>&1
echo "exit=$?"
ls -la $OUT/ 2>/dev/null
head -3 /root/x86guest/preoat_gb.log
