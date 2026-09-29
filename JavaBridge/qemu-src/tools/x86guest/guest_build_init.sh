#!/bin/busybox sh
# 构建版 /init：完成标准初始化后，在 guest 里原生跑 dex2oat（boot 镜像 + 预置 oat），
# 产物经 busybox httpd 暴露给宿主（hostfwd 8080）。构建完成后长期休眠等待取件。
BB=/bin/busybox
export PATH=/system/bin:/bin
getarg() { $BB sed -n 's/.*'"$1"'=\([^ ]*\).*/\1/p' /proc/cmdline 2>/dev/null | $BB head -1; }

$BB mkdir -p /proc /sys /dev /tmp /data/catclaw
$BB mount -t proc proc /proc 2>/dev/null
$BB mount -t sysfs sys /sys 2>/dev/null
$BB mount -t devtmpfs devtmpfs /dev 2>/dev/null || $BB mount -t tmpfs tmpfs /dev -o mode=0755 2>/dev/null
for m in virtio_ring virtio virtio_pci_modern_dev virtio_pci_legacy_dev virtio_pci virtio_blk failover net_failover virtio_net binder_linux; do
    $BB insmod /modules/$m.ko 2>/dev/null
done
echo "=== CatClaw x86 BOOTIMG BUILD begin ==="
$BB ifconfig lo 127.0.0.1 netmask 255.0.0.0 up 2>/dev/null
$BB ifconfig eth0 10.0.2.15 netmask 255.255.255.0 up 2>/dev/null
$BB route add default gw 10.0.2.2 2>/dev/null
mkdir -p /etc
echo "nameserver 10.0.2.3" > /etc/resolv.conf
/fakelogd > /fakelogd.log 2>&1 &
$BB sleep 1

export ANDROID_ROOT=/system ANDROID_DATA=/data ANDROID_ART_ROOT=/apex/com.android.art
export ANDROID_I18N_ROOT=/apex/com.android.i18n ANDROID_TZDATA_ROOT=/apex/com.android.tzdata
export TMPDIR=/data/local/tmp
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
$BB mkdir -p $TMPDIR /system/javalib/x86_64 /data/dalvik-cache/x86_64 /out

JL=/system/javalib
FW=/system/framework
BCP="$JL/core-oj.jar:$JL/core-libart.jar:$JL/core-icu4j.jar:$JL/okhttp.jar:$JL/bouncycastle.jar:$JL/apache-xml.jar:$JL/conscrypt.jar:$FW/framework.jar:$FW/ext.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/android.test.base.jar"
D2O=/apex/com.android.art/bin/dex2oat64

echo "[build] boot 全量编译开始（-j4）"
# 注：不传 --boot-image（首次构建无镜像可用；ART13 该参数没有 no-image 伪值，
# 传了直接 usage error exit=1，2026-09-29 实测）。真实报错走 LOG→logd→/fakelogd.log。
$D2O --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1536m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 \
  --image=$JL/x86_64/boot.art --oat-file=$JL/x86_64/boot.oat \
  --dex-file=$JL/core-oj.jar --dex-location=$JL/core-oj.jar \
  --dex-file=$JL/core-libart.jar --dex-location=$JL/core-libart.jar \
  --dex-file=$JL/core-icu4j.jar --dex-location=$JL/core-icu4j.jar \
  --dex-file=$JL/okhttp.jar --dex-location=$JL/okhttp.jar \
  --dex-file=$JL/bouncycastle.jar --dex-location=$JL/bouncycastle.jar \
  --dex-file=$JL/apache-xml.jar --dex-location=$JL/apache-xml.jar \
  --dex-file=$JL/conscrypt.jar --dex-location=$JL/conscrypt.jar \
  --dex-file=$FW/framework.jar --dex-location=$FW/framework.jar \
  --dex-file=$FW/ext.jar --dex-location=$FW/ext.jar \
  --dex-file=$FW/telephony-common.jar --dex-location=$FW/telephony-common.jar \
  --dex-file=$FW/voip-common.jar --dex-location=$FW/voip-common.jar \
  --dex-file=$FW/ims-common.jar --dex-location=$FW/ims-common.jar \
  --dex-file=$FW/android.hidl.base-V1.0-java.jar --dex-location=$FW/android.hidl.base-V1.0-java.jar \
  --dex-file=$FW/android.hidl.manager-V1.0-java.jar --dex-location=$FW/android.hidl.manager-V1.0-java.jar \
  --dex-file=$FW/android.test.base.jar --dex-location=$FW/android.test.base.jar \
  -j4 2>/out/err-boot.log
rc=$?
[ $rc -ne 0 ] && $BB cat /out/err-boot.log
echo "[build] boot exit=$rc"
$BB ls -la $JL/x86_64/ | head -12

echo "[build] gb.dex 预置 oat（quicken）"
# boot image 用真实文件路径（上一步刚产出的），而非 §6.10 的「首 jar 目录 + boot.art」
# 运行时推导位 —— 那个软链是打包阶段才建的，构建期不存在。
$D2O --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=quicken --boot-image=$JL/x86_64/boot.art \
  --dex-file=/gb.dex --oat-file=/data/dalvik-cache/x86_64/gb.dex --output-vdex=/data/dalvik-cache/x86_64/gb.vdex -j4 2>/out/err-gb.log
rc=$?
[ $rc -ne 0 ] && $BB cat /out/err-gb.log
echo "[build] gb exit=$rc"

echo "[build] tvbox.apk 预置 oat（quicken）"
$D2O --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=quicken --boot-image=$JL/x86_64/boot.art \
  --dex-file=/tvbox.apk --oat-file=/data/dalvik-cache/x86_64/tvbox.apk@classes.dex --output-vdex=/data/dalvik-cache/x86_64/tvbox.apk@classes.vdex -j4 2>/out/err-tvbox.log
rc=$?
[ $rc -ne 0 ] && $BB cat /out/err-tvbox.log
echo "[build] tvbox exit=$rc"

# 产物集中 + HTTP 暴露（宿主经 hostfwd 取）
$BB mkdir -p /out/boot /out/cache
$BB cp $JL/x86_64/boot*.art $JL/x86_64/boot*.oat $JL/x86_64/boot*.vdex /out/boot/ 2>/dev/null
$BB cp /data/dalvik-cache/x86_64/* /out/cache/ 2>/dev/null
# dex2oat 的真实报错走 LOG→logd→fakelogd：一并暴露给宿主
$BB cp /fakelogd.log /out/ 2>/dev/null
$BB cp /out/err-*.log /out/ 2>/dev/null || true
echo "[build] 产物清单："
$BB ls -la /out/boot /out/cache
echo "=== CatClaw x86 BOOTIMG BUILD done ==="
$BB httpd -f -p 8080 -h /out
echo "[build] httpd 已起（8080），等待取件…"
while true; do $BB sleep 3600; done
