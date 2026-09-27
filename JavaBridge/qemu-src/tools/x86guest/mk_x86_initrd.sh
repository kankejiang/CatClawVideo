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
umount $W/mnt 2>/dev/null || true   # 清理上次残留挂载（否则本次 mount 失败、mnt 为空目录 → 后续 cp 全挂）
mount -o loop,ro $W/system.img $W/mnt
cp -a $W/mnt/system/framework $R/system/
cp -a $W/mnt/system/lib64     $R/system/
cp -a $W/mnt/system/bin       $R/system/
cp -a $W/mnt/system/etc       $R/system/
for m in com.android.art com.android.i18n com.android.conscrypt com.android.runtime com.android.os.statsd com.android.tzdata; do
    cp -a "$W/mnt/system/apex/$m" "$R/apex/"
done
# ⚠ 决定性绕行（必须在 umount 之前！曾放在 umount 后导致 cp 全部 stat 失败、
#   rootfs 停在半成品而测试一直跑旧 initrd——2026-09-27 排查 3 轮才定位）：
#   ART 对 BCP 里位于 /apex/ 下的 jar 必须建立对应的 apex linker namespace（极简
#   rootfs 配不齐 → 反复 abort）。把 core/conscrypt jar 拷到 /system/javalib/，
#   BCP 全部走 /system 路径 → 无 apex namespace 需求。
mkdir -p $R/system/javalib
for j in core-oj core-libart okhttp bouncycastle apache-xml; do
    cp -a "$W/mnt/system/apex/com.android.art/javalib/$j.jar" "$R/system/javalib/" || echo "跳过缺失: $j.jar"
done
cp -a $W/mnt/system/apex/com.android.conscrypt/javalib/conscrypt.jar $R/system/javalib/ || echo "跳过缺失: conscrypt.jar"
cp -a $W/mnt/system/apex/com.android.i18n/javalib/core-icu4j.jar $R/system/javalib/ || echo "skip: core-icu4j.jar"
umount $W/mnt

# ── ARM 转译层（libndk_translation，ChromeOS sdk_gphone_x86_64:13 官方抽取）──
# Guard 壳的 arm64 so 靠它转译执行：ART 读 ro.dalvik.vm.native.bridge 属性 dlopen 主库，
# 主库自管 /system/lib64/arm64/ 支持库；binfmt_misc 部分不用（dlopen 不走 execve）。
NDK="$W/vendor_google_proprietary_ndk_translation-prebuilt-11arm_13arm64/prebuilts"
if [ -d "$NDK" ]; then
    cp -a "$NDK/lib64/." "$R/system/lib64/"
    cp -a "$NDK/lib/."   "$R/system/lib/"
    cp -a "$NDK/bin/."   "$R/system/bin/"
    echo "ndk_translation 已并入 rootfs（Guard 转译就绪）"
    # cpuinfo 覆盖文件：ndk initialize 会 bind-mount 到 /proc/cpuinfo（arm64 代码读 Features/型号），
    # ChromeOS 构建时生成、ndk 包未携带，缺失则 bind-mount 失败（实测随后进程死）——伪造简化版
    CI="$R/system/etc/cpuinfo.arm64.txt"
    { echo "processor  : 0"; echo "BogoMIPS   : 100.00"; \
      echo "Features   : fp asimd evtstrm aes pmull sha1 sha2 crc32"; \
      echo "CPU implementer : 0x41"; echo "CPU architecture: 8"; echo "CPU part  : 0xd03"; \
      echo "Hardware  : ndk_translation"; } > "$CI"
    cp "$CI" "$R/system/lib64/arm64/cpuinfo"
    echo "cpuinfo 覆盖文件已生成"
else
    echo "WARN: 缺 ndk_translation prebuilts，Guard 源不可用"
fi
# ── arm64 专用 linker 配置（决定性，2026-09-27）──
# ndk 的 arm64 linker64 按自身 ISA 找 /system/etc/ld.config.arm64.txt，找不到才回落
# /linkerconfig/ld.config.txt（x86 内容）→ 搜到 x86 libc++ → EM 不符 FATAL。
# 这份只给 arm64 侧：搜索路径全指 arm64 子目录；x86 linker 继续用 /linkerconfig 那份，互不干扰。
cat > $R/system/etc/ld.config.arm64.txt <<'ARM64CFG'
dir.system = /system/bin
dir.system = /system/xbin

[system]
namespace.default.isolated = false
namespace.default.search.paths = /system/lib64/arm64
namespace.default.permitted.paths = /system/lib64/arm64:/system/lib64:/system/bin:/data:/data/catclaw

namespace.sphal.isolated = false
namespace.sphal.visible = true
namespace.sphal.search.paths = /system/lib64/arm64
namespace.sphal.permitted.paths = /system/lib64/arm64:/data:/data/catclaw
ARM64CFG
echo "arm64 linker 配置已生成"

# ⚠ 移除 dex2oat64：13 在 boot 镜像缺失时会现场调 dex2oat 生成 boot classpath 镜像，
#   而那个镜像要求 linker namespace 与 APEX 元数据一致（com_android_art），极简 rootfs
#   无法满足 → 反复 abort（2026-09-27 实测 4 轮）。移除后 ART 无条件 imageless+JIT，
#   原生 CPU（WHPX）下 JIT 对 IO 密集的桥完全够用。
rm -f $R/apex/com.android.art/bin/dex2oat64
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
# ui_stub.dex（UI 桩优先链）：Art.stubFirst() 从 /ui_stub.dex 加载，jar 源的 android.*
# 解析到桩而非真 framework（Dialog/Toast 事件才能上行）。⚠ 缺它 Guard 壳初始化即崩
# （2026-09-27：108 崩/用户环境活的唯一差异，mk 脚本此前丢行——与 build_gb_dex.py
# 的产物配套，两处生成的 dm 必须都进 initrd）
[ -f $W/ui_stub.dex ] && cp $W/ui_stub.dex $R/ui_stub.dex
cp $W/TVBox_debug-java64.apk $R/tvbox.apk
# Guard 壳 jar（玩偶）——nativebridge 转译实测用
mkdir -p $R/data/catclaw/art/inbox
[ -f $W/wogg.jar ] && cp $W/wogg.jar $R/data/catclaw/art/inbox/raw-08a27c1fff2c064ae9a12c34.jar
# 非 Guard 同族 jar（宿主 NonGuardFallbackJars 同源，900+ 爬虫类）——原生速度基准用
[ -f $W/fty.jar ] && cp $W/fty.jar $R/fty.jar

# ── 3. 内核模块 ──
# ⚠ 移除 dex2oat64：13 在 boot 镜像缺失时会现场调 dex2oat 生成 boot classpath 镜像，
#   而那个镜像要求 linker namespace 与 APEX 元数据一致（com_android_art），极简 rootfs
#   无法满足 → 反复 abort（2026-09-27 实测 4 轮）。移除后 ART 无条件 imageless+JIT，
#   原生 CPU（WHPX）下 JIT 对 IO 密集的桥完全够用。
rm -f $R/apex/com.android.art/bin/dex2oat64
for m in virtio_ring virtio virtio_pci_modern_dev virtio_pci_legacy_dev virtio_pci virtio_blk net_failover failover virtio_net binder_linux; do
    src=$(find $W/linux-data/lib/modules/6.1.0-50-amd64 -name "$m.ko" 2>/dev/null | head -1)
    [ -n "$src" ] && cp "$src" $R/modules/ && echo "模块: $m"
done

# ── 4. /init 与 /linkerconfig/ld.config.txt ──
cp $W/init_x86.sh $R/init
chmod +x $R/init
# chroot 跑真 linkerconfig 生成标准 [legacy] 配置，再把 APEX 库路径插进 default namespace
# （chroot 环境缺属性区，linkerconfig 走 legacy 分支——不含 /apex，需手工补）
chroot $R /system/bin/linkerconfig > $R/linkerconfig/ld.config.txt 2>/dev/null || true
sed -i 's|namespace.default.search.paths = /system/|namespace.default.search.paths = /apex/com.android.art/${LIB}:/apex/com.android.i18n/${LIB}:/apex/com.android.conscrypt/${LIB}:/apex/com.android.os.statsd/${LIB}:/system/|' $R/linkerconfig/ld.config.txt
sed -i 's|namespace.default.permitted.paths = |namespace.default.permitted.paths = /system/lib64/arm64:|' $R/linkerconfig/ld.config.txt
sed -i 's|namespace.default.permitted.paths = /system/|namespace.default.permitted.paths = /apex/com.android.art/${LIB}:/apex/com.android.i18n/${LIB}:/apex/com.android.os.statsd/${LIB}:/system/|' $R/linkerconfig/ld.config.txt
# ⚠ 自定义 namespace 必须经 additional.namespaces 声明才会建立（bionic 解析规则），
#   否则 ART 找 apex namespace 时报「no namespace called com_android_art」→ abort
cat >> $R/linkerconfig/ld.config.txt <<'APXNS'
additional.namespaces = com_android_art
namespace.com_android_art.isolated = false
namespace.com_android_art.visible = true
namespace.com_android_art.search.paths = /apex/com.android.art/${LIB}
namespace.com_android_art.permitted.paths = /apex/com.android.art/${LIB}:/system/${LIB}:/data:/data/catclaw
APXNS

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
