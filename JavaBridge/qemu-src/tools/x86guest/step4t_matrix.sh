#!/bin/bash
# §6.10 Step4t：559 定位实验矩阵（BCP 路径形态 × dex-file 组数）
cd /root/x86guest
TREE=/root/x86guest/art-tree
FW=$TREE/system/framework

run() {
  local name="$1"; shift
  echo "════ $name ════"
  timeout 40 /usr/bin/qemu-aarch64-static -L $TREE ./dex2oatd "$@" 2>&1 |
    grep -E 'files \([0-9]+\)|locations \([0-9]+\)|took|Successfully' | head -3
  echo "（exit=$?）"
}

# 公共 runtime 参数生成：BCP 用宿主绝对路径
HOST_BCP="$FW/core-oj.jar:$FW/core-libart.jar"
SYS_BCP="/system/framework/core-oj.jar:/system/framework/core-libart.jar"

# ① BCP=宿主路径，无 dex-file（纯 runtime check）
run "①宿主BCP 无dexfile" \
  --runtime-arg -Xbootclasspath:$HOST_BCP \
  --runtime-arg -Xms64m --android-root=$TREE --instruction-set=arm64 \
  --compiler-filter=verify --image=/system/framework/arm64/boot.art \
  --oat-file=/tmp/t1.oat -j2

# ② BCP=/system 路径（原失败形态），无 dex-file
run "②systemBCP 无dexfile" \
  --runtime-arg -Xbootclasspath:$SYS_BCP \
  --runtime-arg -Xms64m --android-root=$TREE --instruction-set=arm64 \
  --compiler-filter=verify --image=/system/framework/arm64/boot.art \
  --oat-file=/tmp/t2.oat -j2

# ③ 宿主 BCP + 2 组 dex-file
run "③宿主BCP 2组dexfile" \
  --runtime-arg -Xbootclasspath:$HOST_BCP \
  --runtime-arg -Xms64m --android-root=$TREE --instruction-set=arm64 \
  --compiler-filter=verify --image=/system/framework/arm64/boot.art \
  --oat-file=/tmp/t3.oat \
  --dex-file=$FW/core-oj.jar --dex-location=/system/framework/core-oj.jar \
  --dex-file=$FW/core-libart.jar --dex-location=/system/framework/core-libart.jar -j2
