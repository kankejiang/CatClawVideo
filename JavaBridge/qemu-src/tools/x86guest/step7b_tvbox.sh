#!/bin/bash
# §6.10 Step7b：预编 tvbox.apk 的 quicken oat（大头，guest 内 14.25s → 宿主 qemu-user 预计 1-3 分钟）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=/system/framework
OUT=/root/x86guest/dalvik-preinit/arm64
: > /tmp/fakelogd.log

BCP="$FW/core-oj.jar:$FW/core-libart.jar:$FW/conscrypt.jar:$FW/okhttp.jar:$FW/bouncycastle.jar:$FW/apache-xml.jar:$FW/ext.jar:$FW/framework.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/framework-oahl-backward-compatibility.jar:$FW/android.test.base.jar"

rm -f "$OUT/tvbox.apk@classes.dex" "$OUT/tvbox.apk@classes.vdex"
nohup strace -f -e trace=mmap -o /tmp/strace7.log \
  /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx512m \
  --android-root=/ --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=quicken \
  --boot-image=/system/framework/boot.art \
  --dex-file=/tvbox.apk \
  --oat-file="$OUT/tvbox.apk@classes.dex" \
  --output-vdex="$OUT/tvbox.apk@classes.vdex" \
  -j1 > /root/x86guest/preoat_tvbox.log 2>&1 &

echo "tvbox 预编 PID=$!  （日志 preoat_tvbox.log）"
sleep 30
ps -eo pid,etime,pcpu --no-headers -p $! >/dev/null 2>&1 && echo '30s 后仍在运行' || echo '!! 已退出'
ls -la $OUT/ 2>/dev/null
