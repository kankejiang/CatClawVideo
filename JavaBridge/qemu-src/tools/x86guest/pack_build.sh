#!/bin/bash
# 构建版 initrd 打包（108）：rootfs 重建 + 恢复 dex2oat64 + 换构建版 /init + 重打包
set -e
W=/root/x86guest
R=$W/rootfs
bash $W/mk_x86_initrd.sh
mkdir -p $W/mnt2
umount $W/mnt2 2>/dev/null || true
mount -o loop,ro $W/system.img $W/mnt2
cp $W/mnt2/system/apex/com.android.art/bin/dex2oat64 $R/apex/com.android.art/bin/
umount $W/mnt2
cp $W/guest_build_init.sh $R/init
chmod +x $R/init
cd $R && find . | cpio -o -H newc --owner 0:0 2>/dev/null | gzip -1 > $W/art_initrd_x64_build.gz
echo "构建版 initrd: $(ls -lh $W/art_initrd_x64_build.gz | awk '{print $5}')"
