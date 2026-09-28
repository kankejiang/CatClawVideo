#!/bin/bash
# §6.10 Step4c：全量 dex2oat 自产 boot 镜像（第一轮 compiler-filter=verify）
# 输入 = vdex 抽出的真 dex（cdex，unquicken 过）；产物 = boot*.art/oat/vdex 落进 art-tree
#（stub jar + vdex 的系统语义自洽，CATCLAW_BCP_LOCATIONS + -Xnorelocate 原位启用）。
set -x
cd /root/x86guest
TREE=/root/x86guest/art-tree
DEX=/root/x86guest/dex-out

# ① 备份旧 boot 镜像三件套（含 .rel；自产 -Xnorelocate 不需要 rel）
mkdir -p boot-backup
mv $TREE/system/framework/boot.art $TREE/system/framework/boot-*.art \
   $TREE/system/framework/arm64/boot.oat $TREE/system/framework/arm64/boot-*.oat \
   $TREE/system/framework/boot.vdex $TREE/system/framework/boot-*.vdex \
   $TREE/system/framework/boot.art.rel $TREE/system/framework/boot-*.art.rel \
   boot-backup/ 2>/dev/null
echo "备份 $(ls boot-backup | wc -l) 个文件"

BCP="/system/framework/core-oj.jar:/system/framework/core-libart.jar:/system/framework/conscrypt.jar:/system/framework/okhttp.jar:/system/framework/bouncycastle.jar:/system/framework/apache-xml.jar:/system/framework/ext.jar:/system/framework/framework.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/framework-oahl-backward-compatibility.jar:/system/framework/android.test.base.jar"

# ② fakelogd 日志通道（dex2oat 的真实日志走它）
mkdir -p /dev/socket
pgrep -f fakelogd >/dev/null || /usr/bin/qemu-aarch64-static -L $TREE ./art-tree/fakelogd > /tmp/fakelogd.log 2>&1 &
sleep 1

# ③ 后台编译（产物按 --android-root 前缀落 art-tree；multidex framework 3 个 dex 同 location）
nohup /usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd \
  --runtime-arg -Xbootclasspath:$BCP \
  --runtime-arg -Xnorelocate \
  --runtime-arg -Xms64m --runtime-arg -Xmx1536m \
  --android-root=$TREE \
  --instruction-set=arm64 --instruction-set-features=default \
  --compiler-filter=verify \
  --base=0x70000000 \
  --image=/system/framework/arm64/boot.art \
  --oat-file=$TREE/system/framework/arm64/boot.oat \
  --image-classes=$TREE/system/etc/preloaded-classes \
  --dex-file=$DEX/boot_classes.cdex --dex-location=/system/framework/core-oj.jar \
  --dex-file=$DEX/boot-core-libart_classes.cdex --dex-location=/system/framework/core-libart.jar \
  --dex-file=$DEX/boot-conscrypt_classes.cdex --dex-location=/system/framework/conscrypt.jar \
  --dex-file=$DEX/boot-okhttp_classes.cdex --dex-location=/system/framework/okhttp.jar \
  --dex-file=$DEX/boot-bouncycastle_classes.cdex --dex-location=/system/framework/bouncycastle.jar \
  --dex-file=$DEX/boot-apache-xml_classes.cdex --dex-location=/system/framework/apache-xml.jar \
  --dex-file=$DEX/boot-ext_classes.cdex --dex-location=/system/framework/ext.jar \
  --dex-file=$DEX/boot-framework_classes.cdex --dex-location=/system/framework/framework.jar \
  --dex-file=$DEX/boot-framework_classes2.cdex --dex-location=/system/framework/framework.jar \
  --dex-file=$DEX/boot-framework_classes3.cdex --dex-location=/system/framework/framework.jar \
  --dex-file=$DEX/boot-telephony-common_classes.cdex --dex-location=/system/framework/telephony-common.jar \
  --dex-file=$DEX/boot-voip-common_classes.cdex --dex-location=/system/framework/voip-common.jar \
  --dex-file=$DEX/boot-ims-common_classes.cdex --dex-location=/system/framework/ims-common.jar \
  --dex-file=$DEX/boot-android.hidl.base-V1.0-java_classes.cdex --dex-location=/system/framework/android.hidl.base-V1.0-java.jar \
  --dex-file=$DEX/boot-android.hidl.manager-V1.0-java_classes.cdex --dex-location=/system/framework/android.hidl.manager-V1.0-java.jar \
  --dex-file=$DEX/boot-framework-oahl-backward-compatibility_classes.cdex --dex-location=/system/framework/framework-oahl-backward-compatibility.jar \
  --dex-file=$DEX/boot-android.test.base_classes.cdex --dex-location=/system/framework/android.test.base.jar \
  -j4 > /root/x86guest/dex2oat_prod.log 2>&1 &

echo "dex2oat PID=$!"
sleep 8
echo '=== 启动日志 ==='
tail -8 /root/x86guest/dex2oat_prod.log
echo '=== fakelogd 尾部（真实报错通道）==='
tail -5 /tmp/fakelogd.log
