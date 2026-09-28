#!/bin/bash
# §6.10 Step6f：全量 multi-image 编译（干净产物统一）
#   · 形态：guest 视角路径（--android-root=/ + /system/…，/system 软链到 art-tree）
#   · qemu-user：-B 0x4000000000 -R 0x40000000000；strace 包裹（避 ART LOS mmap SIGABRT）
#   · 上轮实测：qemu-user 单线程 verify 55MB ≈ 195s
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=/system/framework
[ -e /system ] || ln -s $TREE/system /system
mkdir -p /dev/socket
: > /tmp/fakelogd.log

# 清空全部 boot 产物（主件 + 15 分件），保证本轮产物完整一致
rm -f $TREE/system/framework/arm64/boot*.art \
      $TREE/system/framework/arm64/boot*.oat \
      $TREE/system/framework/arm64/boot*.vdex

BCP="$FW/core-oj.jar:$FW/core-libart.jar:$FW/conscrypt.jar:$FW/okhttp.jar:$FW/bouncycastle.jar:$FW/apache-xml.jar:$FW/ext.jar:$FW/framework.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/framework-oahl-backward-compatibility.jar:$FW/android.test.base.jar"

cd /root/x86guest
nohup strace -f -e trace=mmap -o /tmp/strace6.log \
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
  -j1 > /root/x86guest/dex2oat_full.log 2>&1 &

PID=$!
echo "全量编译已后台启动 PID=$PID（日志 /root/x86guest/dex2oat_full.log，实测约 195s）"
sleep 25
if ps -p $PID > /dev/null; then
  echo "25s 后仍在运行 ✓"
  ps -eo pid,etime,pcpu,args --no-headers | grep dex2oatd | grep -v grep | head -1 | cut -c1-120
else
  echo "!! 25s 内已退出，检查日志："
  tail -5 /root/x86guest/dex2oat_full.log
fi
