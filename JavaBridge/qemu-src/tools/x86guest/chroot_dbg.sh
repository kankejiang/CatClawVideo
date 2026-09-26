#!/bin/bash
cd /root/x86guest || exit 1
mount -o loop,ro system.img mnt 2>/dev/null
mount -t proc proc rootfs/proc 2>/dev/null
mount --bind /dev rootfs/dev 2>/dev/null
mount -t sysfs sys rootfs/sys 2>/dev/null
echo ==== chroot artlaunch ====
chroot rootfs /bin/busybox sh -c "export PATH=/system/bin:/bin; export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64; export CATCLAW_BCP=/system/javalib/core-oj.jar:/system/javalib/core-libart.jar:/system/javalib/okhttp.jar:/system/javalib/bouncycastle.jar:/system/javalib/conscrypt.jar:/system/framework/framework.jar:/system/framework/ext.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/android.test.base.jar; export CATCLAW_JVM_EXTRA=-Xnoimage-dex2oat -Xnodex2oat; export ANDROID_ROOT=/system ANDROID_DATA=/data ANDROID_ART_ROOT=/apex/com.android.art; export TMPDIR=/data/local/tmp; export LD_PRELOAD=/proppreload.so; timeout 20 /system/bin/artlaunch bridge.GuestMain /gb.dex:/tvbox.apk 18600 2>&1" | tail -25
echo ==== javalib ====
ls rootfs/system/javalib/
umount rootfs/proc 2>/dev/null; umount rootfs/dev 2>/dev/null; umount rootfs/sys 2>/dev/null
umount mnt 2>/dev/null
