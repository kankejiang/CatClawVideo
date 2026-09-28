#!/bin/bash
# §6.10 Step4d：把抽出的真 dex 塞回 stub jar → dex2oat 的 Runtime 初始化能打开 BCP
# （stub jar 无 dex 是 dex2oat usage error 的死因；运行时也从此更自洽——
#  jar 内 dex checksum 与自产 boot.oat 同源必然对齐。原 stub 已留 boot-backup。）
set -x
cd /root/x86guest
TREE=/root/x86guest/art-tree
DEX=/root/x86guest/dex-out
FW=$TREE/system/framework

which zip || apt-get install -y zip 2>&1 | tail -1

# jar 名 → vdex 前缀（core-oj 特殊 = boot 主件）
declare -A MAP=(
  [core-oj]=boot
  [core-libart]=boot-core-libart
  [conscrypt]=boot-conscrypt
  [okhttp]=boot-okhttp
  [bouncycastle]=boot-bouncycastle
  [apache-xml]=boot-apache-xml
  [ext]=boot-ext
  [framework]=boot-framework
  [telephony-common]=boot-telephony-common
  [voip-common]=boot-voip-common
  [ims-common]=boot-ims-common
  [android.hidl.base-V1.0-java]=boot-android.hidl.base-V1.0-java
  [android.hidl.manager-V1.0-java]=boot-android.hidl.manager-V1.0-java
  [framework-oahl-backward-compatibility]=boot-framework-oahl-backward-compatibility
  [android.test.base]=boot-android.test.base
)

for jar in "${!MAP[@]}"; do
  v=${MAP[$jar]}
  n=$(ls $DEX/${v}_classes*.cdex 2>/dev/null | wc -l)
  if [ "$n" -eq 0 ]; then echo "!! $jar 无 dex，跳过"; continue; fi
  # multidex：classes/classes2/classes3 顺序按文件名
  rm -rf /tmp/inj; mkdir -p /tmp/inj; i=1
  for f in $(ls $DEX/${v}_classes*.cdex | sort); do
    if [ $i -eq 1 ]; then cp "$f" /tmp/inj/classes.dex; else cp "$f" /tmp/inj/classes$i.dex; fi
    i=$((i+1))
  done
  (cd /tmp/inj && zip -q -j $FW/$jar.jar classes*.dex)
  echo "$jar.jar ← ${v} (${n} dex, $(du -h $FW/$jar.jar | cut -f1))"
done

echo '=== 塞好后 jar 大小一览 ==='
du -h $FW/core-oj.jar $FW/framework.jar $FW/ext.jar 2>/dev/null
