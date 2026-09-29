#!/bin/bash
# §6.12 x86 boot 镜像 + 预置 oat 构建（108 上跑，qemu-user 路线）。
# 产物：/root/x86guest/bootimg_artifacts.cpio.gz —— 只含 system/javalib/x86_64/ 与
#       data/dalvik-cache/x86_64/ 两个目录树，Windows 侧 build_bootimg_inject.py
#       把它注入生产 initrd（含 init 的 -Ximage 补丁与 libart 验证器软失败补丁）。
#
# ★ 为什么走 qemu-user（2026-09-29 定论）：宿主 chroot 原生跑 dex2oat64 与 guest 内原生
#   跑，都在 Runtime::CreateResolutionMethod 的 LinearAlloc 首次分配 mmap ENOMEM abort——
#   低 2GB 被 dex2oat 自身的 2GB（fixed 0x12c00000）+64MB（0xec00000）预留占满，strace
#   无效。qemu-x86_64-static 用户态仿真下 guest 地址空间由 qemu 管理，预留/分配不再撞车，
#   boot 全量编译 exit=0（约 5 分钟）。
# 用法：bash build_bootimg_x108.sh        （可重复跑，幂等重建）
set -e
W=/root/x86guest
R=$W/rootfs
ISA=x86_64
QEMU=/usr/bin/qemu-x86_64-static
[ -x $QEMU ] || { echo "缺 $QEMU（apt install qemu-user-static）"; exit 1; }

# ── 0) 前置清理：历史运行泄漏的挂载（中止时 trap 未必已注册）——必须在 mk 的 rm 之前 ──
R2=$R
for i in 1 2 3 4 5; do
  mount | grep -q 'x86guest/rootfs' || break
  umount -l $R2/proc $R2/sys $R2/dev 2>/dev/null
  sleep 1
done
mount | grep -q 'x86guest/rootfs' && { echo "rootfs 挂载清不掉，中止"; exit 1; }
rm -rf $R2
trap 'umount -l $R/proc $R/sys $R/dev 2>/dev/null' EXIT

# ── 0b) 全新 rootfs（含生产 init；mk 末尾会打 baseline initrd，无妨）+ dex2oat64 恢复 ──
bash $W/mk_x86_initrd.sh > /dev/null 2>&1 || bash $W/mk_x86_initrd.sh
mkdir -p $W/mnt2
umount $W/mnt2 2>/dev/null || true
mount -o loop,ro $W/system.img $W/mnt2
# mk 可能已用硬链接铺过 apex（cp 报「同一文件」= 源目的同 inode，set -e 下会误杀脚本）
cp -f $W/mnt2/system/apex/com.android.art/bin/dex2oat64 $R/apex/com.android.art/bin/ 2>/dev/null || true
cp -f $W/mnt2/system/apex/com.android.art/lib64/*.so $R/apex/com.android.art/lib64/ 2>/dev/null || true
umount $W/mnt2
cp $QEMU $R/usr/bin/ 2>/dev/null || { mkdir -p $R/usr/bin; cp $QEMU $R/usr/bin/; }

# ── 1) 挂载（chroot 前置；结束统一卸载）──
mount -t proc proc $R/proc
mount -t sysfs sys $R/sys
umount $R/dev 2>/dev/null || true
mount -t tmpfs tmpfs $R/dev
mknod -m 666 $R/dev/null c 1 3 2>/dev/null || true
mknod -m 666 $R/dev/urandom c 1 9 2>/dev/null || true
mkdir -p $R/dev/socket $R/data/local/tmp

# ── 2) boot 全量编译（BCP 15 项，verify 滤镜，multi-image，qemu-user -j4）──
cat > $R/bb_boot.sh <<'EOF'
#!/bin/busybox sh
export PATH=/system/bin:/bin:/usr/bin
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
export ANDROID_ROOT=/system ANDROID_ART_ROOT=/apex/com.android.art ANDROID_DATA=/data
export TMPDIR=/data/local/tmp; mkdir -p $TMPDIR
/fakelogd > /fl_boot.log 2>&1 &
sleep 1
JL=/system/javalib
FW=/system/framework
BCP="$JL/core-oj.jar:$JL/core-libart.jar:$JL/core-icu4j.jar:$JL/okhttp.jar:$JL/bouncycastle.jar:$JL/apache-xml.jar:$JL/conscrypt.jar:$FW/framework.jar:$FW/ext.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/android.test.base.jar"
mkdir -p $JL/x86_64
rm -f $JL/x86_64/boot*.art $JL/x86_64/boot*.oat $JL/x86_64/boot*.vdex
# 不传 --boot-image：首次构建无镜像可用（ART13 该参数没有 no-image 伪值，传了 usage error）
# ⚠ 必须经 qemu-x86_64-static 转译：裸跑（chroot 原生）= LinearAlloc 低 2GB mmap ENOMEM
#   abort exit=134（2026-09-29 晚实测复现，正是当年 chroot 路线的死法）
Q=/usr/bin/qemu-x86_64-static
$Q -L / /apex/com.android.art/bin/dex2oat64 \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=verify --base=0x70000000 \
  --image=$JL/x86_64/boot.art \
  --oat-file=$JL/x86_64/boot.oat \
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
  -j4
echo "boot exit=$?"
# 守门：boot.art 空/缺失（dex2oat 崩）时禁止继续（预置 oat 对坏镜像无意义）
# （正常 boot.art ≈ 896KB，阈值取 500KB）
[ -s $JL/x86_64/boot.art ] && [ $(stat -c %s $JL/x86_64/boot.art) -gt 500000 ] || { echo "!! boot.art 缺失/过小，中止"; exit 1; }
ls -la $JL/x86_64/ | head -6
EOF
chmod +x $R/bb_boot.sh
echo "═══ boot 全量编译（qemu-user，约 5 分钟）═══"
# ⚠ 不要用 tail 管道截断——dex2oat 的报错就在全量输出里（2026-09-29 晚：0 字节产物 +
#   报错被 tail 吃掉，白跑一轮）
chroot $R /bin/busybox sh -c "/bb_boot.sh" 2>&1

# ── 3) 预置 oat：gb.dex + tvbox.apk quicken（对刚产出的真实 boot image）──
cat > $R/bb_preoat.sh <<'EOF'
#!/bin/busybox sh
export PATH=/system/bin:/bin:/usr/bin
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
export ANDROID_ROOT=/system ANDROID_ART_ROOT=/apex/com.android.art ANDROID_DATA=/data
export TMPDIR=/data/local/tmp
/fakelogd > /fl_preoat.log 2>&1 &
sleep 1
JL=/system/javalib
FW=/system/framework
BCP="$JL/core-oj.jar:$JL/core-libart.jar:$JL/core-icu4j.jar:$JL/okhttp.jar:$JL/bouncycastle.jar:$JL/apache-xml.jar:$JL/conscrypt.jar:$FW/framework.jar:$FW/ext.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/android.test.base.jar"
mkdir -p /data/dalvik-cache/x86_64
Q=/usr/bin/qemu-x86_64-static
# ★ --boot-image 指真实文件：javalib 根软链是打包阶段才建的（ injector 建），preoat 时不存在
$Q -L / /apex/com.android.art/bin/dex2oat64 \
  --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=quicken --boot-image=$JL/x86_64/boot.art \
  --dex-file=/gb.dex --oat-file=/data/dalvik-cache/x86_64/gb.dex --output-vdex=/data/dalvik-cache/x86_64/gb.vdex -j4
echo "gb exit=$?"
$Q -L / /apex/com.android.art/bin/dex2oat64 \
  --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \
  --android-root=/ --instruction-set=x86_64 --instruction-set-features=default \
  --compiler-filter=quicken --boot-image=$JL/x86_64/boot.art \
  --dex-file=/tvbox.apk --oat-file=/data/dalvik-cache/x86_64/tvbox.apk@classes.dex --output-vdex=/data/dalvik-cache/x86_64/tvbox.apk@classes.vdex -j4
echo "tvbox exit=$?"
ls -la /data/dalvik-cache/x86_64/
EOF
chmod +x $R/bb_preoat.sh
echo "═══ 预置 oat（约 2 分钟）═══"
chroot $R /bin/busybox sh -c "/bb_preoat.sh" 2>&1

# ── 4) 只打产物目录（干净卸载后；init 注入与 libart 补丁在 Windows 侧 injector 做）──
umount $R/proc $R/sys $R/dev 2>/dev/null || true
cd $R
find ./system/javalib/x86_64 ./data/dalvik-cache/x86_64 | cpio -o -H newc --owner 0:0 2>/dev/null | gzip -1 > $W/bootimg_artifacts.cpio.gz
echo "产物: $(ls -lh $W/bootimg_artifacts.cpio.gz | awk '{print $5, $9}')"
echo "ALL-DONE"
