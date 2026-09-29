#!/bin/bash
# §6.11 x86 boot 镜像 + 预置 oat 构建（Windows 仓库 .zwork/build_boot_x86.sh → scp 到 108 执行）
# 用法: bash build_boot_x86.sh <stage>   stage ∈ {prepare, boot, preoat, pack}
# 前提: /root/x86guest/{system.img, mk_x86_initrd.sh, init_x86.sh, gb.dex, TVBox_debug-java64.apk}
set -e
W=/root/x86guest
R=$W/rootfs
FW=/system/framework
JL=/system/javalib
ISA=x86_64

# 运行时 BCP（= 部署 init 的 CATCLAW_BCP，逐字对齐——顺序与集合不可改）
BCP="$JL/core-oj.jar:$JL/core-libart.jar:$JL/core-icu4j.jar:$JL/okhttp.jar:$JL/bouncycastle.jar:$JL/apache-xml.jar:$JL/conscrypt.jar:$FW/framework.jar:$FW/ext.jar:$FW/telephony-common.jar:$FW/voip-common.jar:$FW/ims-common.jar:$FW/android.hidl.base-V1.0-java.jar:$FW/android.hidl.manager-V1.0-java.jar:$FW/android.test.base.jar"

case "${1:-}" in
prepare)
  echo "═══ Stage prepare：重建 rootfs + 恢复 dex2oat64 工具链 ═══"
  bash $W/mk_x86_initrd.sh   # 全新 rootfs（与部署 initrd 同源）；末尾会打一个 baseline initrd，无妨
  # mk 脚本删了 dex2oat64——从 Waydroid 源镜像恢复 release 工具链（与树内 libart 同构建）
  mkdir -p $W/mnt2
  umount $W/mnt2 2>/dev/null || true
  mount -o loop,ro $W/system.img $W/mnt2
  APX=/mnt2/system/apex/com.android.art
  cp $W/mnt2/system/apex/com.android.art/bin/dex2oat64 $R/apex/com.android.art/bin/
  # ART apex 自家 lib64 全量覆盖（同一构建，保证 dex2oat64 与运行时 ABI 一致；不动 ndk_translation 的库）
  cp $W/mnt2/system/apex/com.android.art/lib64/*.so $R/apex/com.android.art/lib64/
  umount $W/mnt2
  echo "── chroot 冒烟：dex2oat64 --help ──"
  mount -t proc proc $R/proc
  mount --bind /dev $R/dev
  mount -t sysfs sys $R/sys
  chroot $R /bin/busybox sh -c "export PATH=/system/bin:/bin; export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64; export ANDROID_ROOT=/system ANDROID_ART_ROOT=/apex/com.android.art ANDROID_DATA=/data; mkdir -p /data/local/tmp; export TMPDIR=/data/local/tmp; /apex/com.android.art/bin/dex2oat64 --help 2>&1 | head -5"
  echo "prepare ✓"
  ;;

boot)
  echo "═══ Stage boot：BCP 全量编译（verify 滤镜, 原生 8 线程；ART13 无 multi-image）═══"
  mount -t proc proc $R/proc 2>/dev/null
  mount -t sysfs sys $R/sys 2>/dev/null
  # /dev 用 tmpfs（不绑宿主）：/dev/socket 留给 fakelogd 建 logd 套接字，dex2oat 的
  # usage/错误详情走 LOG(ERROR)→logd，没有 fakelogd 就只剩一句 "See log for usage error"
  umount $R/dev 2>/dev/null || true
  mount -t tmpfs tmpfs $R/dev
  mknod -m 666 $R/dev/null c 1 3 2>/dev/null || true
  mknod -m 666 $R/dev/random c 1 8 2>/dev/null || true
  mknod -m 666 $R/dev/urandom c 1 9 2>/dev/null || true
  mknod -m 666 $R/dev/zero c 1 5 2>/dev/null || true
  mkdir -p $R/dev/socket $R/system/javalib/$ISA
  cat > $R/build_boot.sh <<EOF
#!/bin/busybox sh
export PATH=/system/bin:/bin
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
export ANDROID_ROOT=/system ANDROID_ART_ROOT=/apex/com.android.art ANDROID_DATA=/data
export TMPDIR=/data/local/tmp; mkdir -p \$TMPDIR
/fakelogd &
sleep 1
rm -f $JL/$ISA/boot*.art $JL/$ISA/boot*.oat $JL/$ISA/boot*.vdex
/apex/com.android.art/bin/dex2oat64 \\
  --runtime-arg -Xbootclasspath:$BCP \\
  --runtime-arg -Xnorelocate \\
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \\
  --android-root=/ --instruction-set=$ISA --instruction-set-features=default \\
  --compiler-filter=verify --base=0x70000000 \\
  --image=$JL/$ISA/boot.art \\
  --oat-file=$JL/$ISA/boot.oat \\
  --dex-file=$JL/core-oj.jar --dex-location=$JL/core-oj.jar \\
  --dex-file=$JL/core-libart.jar --dex-location=$JL/core-libart.jar \\
  --dex-file=$JL/core-icu4j.jar --dex-location=$JL/core-icu4j.jar \\
  --dex-file=$JL/okhttp.jar --dex-location=$JL/okhttp.jar \\
  --dex-file=$JL/bouncycastle.jar --dex-location=$JL/bouncycastle.jar \\
  --dex-file=$JL/apache-xml.jar --dex-location=$JL/apache-xml.jar \\
  --dex-file=$JL/conscrypt.jar --dex-location=$JL/conscrypt.jar \\
  --dex-file=$FW/framework.jar --dex-location=$FW/framework.jar \\
  --dex-file=$FW/ext.jar --dex-location=$FW/ext.jar \\
  --dex-file=$FW/telephony-common.jar --dex-location=$FW/telephony-common.jar \\
  --dex-file=$FW/voip-common.jar --dex-location=$FW/voip-common.jar \\
  --dex-file=$FW/ims-common.jar --dex-location=$FW/ims-common.jar \\
  --dex-file=$FW/android.hidl.base-V1.0-java.jar --dex-location=$FW/android.hidl.base-V1.0-java.jar \\
  --dex-file=$FW/android.hidl.manager-V1.0-java.jar --dex-location=$FW/android.hidl.manager-V1.0-java.jar \\
  --dex-file=$FW/android.test.base.jar --dex-location=$FW/android.test.base.jar \\
  -j8
EOF
  chmod +x $R/build_boot.sh
  # ⚠ strace 包裹是必须的：无它时 dex2oat 内部 runtime 在 LinearAlloc 首次分配即
  #   mmap ENOMEM abort（aarch64 step6f 同款坑，见 §6.6/6.10——「避 ART LOS mmap SIGABRT」）
  time strace -f -e trace=mmap -o $W/strace_boot.log chroot $R /bin/busybox sh -c "/build_boot.sh" > $W/boot_build.log 2>&1 || true
  echo "── 产物 ──"
  chroot $R /bin/busybox ls -la $JL/$ISA/ | head -20
  echo "── 日志尾 ──"
  tail -6 $W/boot_build.log
  ;;

preoat)
  echo "═══ Stage preoat：gb.dex + tvbox.apk 预置 oat（quicken）═══"
  mount -t proc proc $R/proc 2>/dev/null
  mount -t sysfs sys $R/sys 2>/dev/null
  umount $R/dev 2>/dev/null || true
  mount -t tmpfs tmpfs $R/dev
  mknod -m 666 $R/dev/null c 1 3 2>/dev/null || true
  mknod -m 666 $R/dev/random c 1 8 2>/dev/null || true
  mknod -m 666 $R/dev/urandom c 1 9 2>/dev/null || true
  mknod -m 666 $R/dev/zero c 1 5 2>/dev/null || true
  mkdir -p $R/dev/socket
  cat > $R/preoat.sh <<EOF
#!/bin/busybox sh
export PATH=/system/bin:/bin
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
export ANDROID_ROOT=/system ANDROID_ART_ROOT=/apex/com.android.art ANDROID_DATA=/data
export TMPDIR=/data/local/tmp
/fakelogd &
sleep 1
mkdir -p /data/dalvik-cache/$ISA
D2O=/apex/com.android.art/bin/dex2oat64
# ★ --boot-image 必须指真实文件（$JL/$ISA/boot.art）：javalib 根的 boot.art 软链是
#   pack 阶段才建的，preoat 时不存在 → 指它会像无镜像一样直接 SIGABRT（2026-09-29 实测）。
\$D2O --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \\
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \\
  --android-root=/ --instruction-set=$ISA --instruction-set-features=default \\
  --compiler-filter=quicken --boot-image=$JL/$ISA/boot.art \\
  --dex-file=/gb.dex --oat-file=/data/dalvik-cache/$ISA/gb.dex --output-vdex=/data/dalvik-cache/$ISA/gb.vdex -j4
echo "gb exit=\$?"
\$D2O --runtime-arg -Xbootclasspath:$BCP --runtime-arg -Xnorelocate \\
  --runtime-arg -Xms32m --runtime-arg -Xmx1024m \\
  --android-root=/ --instruction-set=$ISA --instruction-set-features=default \\
  --compiler-filter=quicken --boot-image=$JL/$ISA/boot.art \\
  --dex-file=/tvbox.apk --oat-file=/data/dalvik-cache/$ISA/tvbox.apk@classes.dex --output-vdex=/data/dalvik-cache/$ISA/tvbox.apk@classes.vdex -j4
echo "tvbox exit=\$?"
EOF
  chmod +x $R/preoat.sh
  time chroot $R /bin/busybox sh -c "/preoat.sh" > $W/preoat.log 2>&1 || true
  chroot $R /bin/busybox ls -la /data/dalvik-cache/$ISA/
  tail -4 $W/preoat.log
  ;;

pack)
  echo "═══ Stage pack：框架根符号链接 + init 改造 + 重打包 ═══"
  umount $R/proc $R/sys $R/dev 2>/dev/null || true
  # 1) 框架根符号链接（javalib 根）：boot*.art|oat|vdex → x86_64/
  chroot $R /bin/busybox sh -c "
    cd $JL
    for f in $ISA/boot*.art $ISA/boot*.oat $ISA/boot*.vdex; do
      [ -e \"\$f\" ] || continue
      b=\${f#$ISA/}
      ln -sf \"\$f\" \"\$b\"
    done
    ls -la $JL/boot.art $JL/boot-2.art 2>/dev/null | head -4
  "
  # 2) init：去 -Xnoimage/-Xnodex2oat，加 -Xnorelocate + BCP_LOCATIONS（运行时动态枚举）
  python3 - <<'PYEOF'
import re
p = "/root/x86guest/rootfs/init"
s = open(p, encoding="utf-8").read()
s = re.sub(r'export CATCLAW_JVM_EXTRA="[^\"]*"',
           'export CATCLAW_JVM_EXTRA="-Xnorelocate"\n'
           '# boot 镜像组件位置（multi-image）：运行时枚举，镜像缺失时为空串=自动回落 imageless\n'
           'LOC=""; for f in /system/javalib/x86_64/boot*.art; do [ -e "$f" ] && LOC="$LOC$f:"; done\n'
           'export CATCLAW_BCP_LOCATIONS="${LOC%:}"', s)
open(p, "w", encoding="utf-8").write(s)
print("init 已改造")
PYEOF
  grep -n 'CATCLAW_JVM_EXTRA\|BCP_LOCATIONS' $R/init | head -4
  # 3) 重打包（与 mk 同命令）
  cd $R && find . | cpio -o -H newc --owner 0:0 2>/dev/null | gzip -1 > $W/art_initrd_x64_boot.gz
  echo "产物: $(ls -lh $W/art_initrd_x64_boot.gz | awk '{print $5}')"
  ;;
*)
  echo "用法: bash build_boot_x86.sh prepare|boot|preoat|pack"
  ;;
esac
