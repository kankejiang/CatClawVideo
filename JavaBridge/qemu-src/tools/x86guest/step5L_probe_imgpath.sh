#!/bin/bash
# §6.10 Step5L：2-dex 快编探针——确定 --android-root/--image 的正确形态（image location 记录正确与否）
# 方案 A：android-root = system 目录本身（/root/.../art-tree/system），image = 其下绝对路径（宿主形态）
# 方案 B：android-root = host-root 映射（/），image = guest 形态 /system/...（需 /system 软链）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework
mkdir -p /dev/socket

# /system 软链（方案 B 需要；不影响方案 A）
[ -e /system ] || ln -s $TREE/system /system

run_probe() {
  name=$1; aroot=$2; img=$3; oatf=$4; df1=$5; df2=$6
  : > /tmp/fakelogd.log
  rm -f $img $oatf
  timeout 120 /usr/bin/qemu-aarch64-static -B 0x4000000000 -R 0x40000000000 -L $TREE ./dex2oatd \
    --runtime-arg -Xbootclasspath:$df1:$df2 \
    --runtime-arg -Xnorelocate \
    --runtime-arg -Xms32m --runtime-arg -Xmx512m \
    --android-root=$aroot --instruction-set=arm64 --instruction-set-features=default \
    --compiler-filter=verify --base=0x70000000 \
    --image=$img --oat-file=$oatf \
    --dex-file=$df1 --dex-location=/system/framework/core-oj.jar \
    --dex-file=$df2 --dex-location=/system/framework/core-libart.jar \
    -j1 > /dev/null 2>&1
  echo "== $name exit=$? =="
  strings -n 12 $oatf 2>/dev/null | grep -a 'boot.*art' | grep -av '^\/system\/framework\/\?[a-z]' | head -2
  strings -n 12 $oatf 2>/dev/null | grep -aE '^\S*boot(-core-libart)?\.art' | head -2
}

echo '──────── 方案 A：android-root=…/art-tree/system、image=宿主绝对路径 ────────'
run_probe A "$TREE/system" "$FW/arm64/boot.art" "$FW/arm64/boot.oat" "$FW/core-oj.jar" "$FW/core-libart.jar"
echo
echo '──────── 方案 B：android-root=/、image=/system/…（guest 形态） ────────'
run_probe B / /system/framework/arm64/boot.art /system/framework/arm64/boot.oat /system/framework/core-oj.jar /system/framework/core-libart.jar
