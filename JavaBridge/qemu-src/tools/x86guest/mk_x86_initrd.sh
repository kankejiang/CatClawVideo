#!/bin/bash
# x86 mini guest initrd 组装（v1）。
# 输入（/root/x86guest/）：system.img、busybox、artlaunch.x64、proppreload_x64.so、
#   fakelogd.x64、gb.dex、TVBox_debug-java64.apk、init_x86.sh、ld_config_x86.txt、
#   linux-data/（Debian 6.1 内核模块）
# 输出：/root/x86guest/art_initrd_x64.gz
set -e
W=/root/x86guest
R=$W/rootfs
rm -rf "$R"; mkdir -p "$R/system" "$R/apex"

# ── 1. Waydroid Android 13 rootfs 子集 ──
mkdir -p $W/mnt
mount -o loop,ro $W/system.img $W/mnt
cp -a $W/mnt/system/framework $R/system/
cp -a $W/mnt/system/lib64     $R/system/
cp -a $W/mnt/system/bin       $R/system/
cp -a $W/mnt/system/etc       $R/system/
for m in com.android.art com.android.i18n com.android.conscrypt com.android.runtime com.android.os.statsd; do
    cp -a "$W/mnt/system/apex/$m" "$R/apex/"
done
umount $W/mnt
# 排除预编译 boot 镜像：内嵌 APEX 版本字段与运行时校验不匹配 → abort；走 imageless+JIT
rm -f $R/system/framework/boot*.art $R/system/framework/boot*.oat \
      $R/system/framework/boot*.vdex $R/system/framework/boot*.bprof
rm -rf $R/system/framework/x86_64 $R/system/framework/arm64
find $R/apex/com.android.art \( -name 'boot*.art' -o -name 'boot*.oat' -o -name 'boot*.vdex' \) -delete 2>/dev/null || true

# ── 2. 桥原生件与基础目录（Windows scp 无 exec 位，必须 chmod）──
mkdir -p $R/bin $R/modules $R/dev $R/proc $R/sys $R/tmp
mkdir -p $R/data/catclaw $R/data/local/tmp $R/data/dalvik-cache/x86_64 $R/data/misc $R/data/system
# linkerconfig 会 realpath 这些分区目录，缺了整段失败：
mkdir -p $R/product $R/system_ext $R/odm $R/vendor $R/system/system_ext $R/linkerconfig
cp $W/busybox $R/bin/busybox
cp $W/artlaunch.x64      $R/artlaunch
cp $W/artlaunch.x64      $R/system/bin/artlaunch
cp $W/proppreload_x64.so $R/proppreload.so
cp $W/fakelogd.x64       $R/fakelogd
chmod +x $R/bin/busybox $R/artlaunch $R/system/bin/artlaunch $R/fakelogd
cp $W/gb.dex $R/gb.dex
cp $W/TVBox_debug-java64.apk $R/tvbox.apk

# ── 3. 内核模块 ──
for m in virtio_ring virtio virtio_pci_modern_dev virtio_pci_legacy_dev virtio_pci virtio_blk net_failover failover virtio_net binder_linux; do
    src=$(find $W/linux-data/lib/modules/6.1.0-50-amd64 -name "$m.ko" 2>/dev/null | head -1)
    [ -n "$src" ] && cp "$src" $R/modules/ && echo "模块: $m"
done

# ── 4. /init 与 /linkerconfig/ld.config.txt ──
cp $W/init_x86.sh $R/init
chmod +x $R/init
cp $W/ld_config_x86.txt $R/linkerconfig/ld.config.txt

# ── 5. /apex/apex-info-list.xml（linkerconfig 元数据）──
python3 - <<PYEOF
import glob, os
R = "$R"
names = sorted(os.path.basename(os.path.dirname(m))
               for m in glob.glob(os.path.join(R, "apex", "*", "apex_manifest.pb")))
items = "".join('<apex-info moduleName="%s" versionCode="1" versionName="1" isFactory="true" isActive="true" lastUpdateSeconds="0" originalPath="/apex/%s"/>' % (n, n) for n in names)
open(os.path.join(R, "apex", "apex-info-list.xml"), "w").write(
    '<?xml version="1.0" encoding="utf-8"?><apex-info-list>%s</apex-info-list>' % items)
print("apex-info-list.xml: %d modules" % len(names))
PYEOF

# ── 6. cpio.gz ──
cd $R && find . | cpio -o -H newc --owner 0:0 2>/dev/null | gzip -1 > $W/art_initrd_x64.gz
echo "产物: $(ls -lh $W/art_initrd_x64.gz | awk '{print $5}')  rootfs 未压缩: $(du -sh $R | cut -f1)"
