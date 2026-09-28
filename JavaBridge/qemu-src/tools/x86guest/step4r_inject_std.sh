#!/bin/bash
# §6.10 Step4r：标准 dex 注入 15 jar（幂等重跑）+ 立即验证
set -x
cd /root/x86guest
FW=/root/x86guest/art-tree/system/framework
declare -A MAP=(
  [core-oj]=boot [core-libart]=boot-core-libart [conscrypt]=boot-conscrypt
  [okhttp]=boot-okhttp [bouncycastle]=boot-bouncycastle [apache-xml]=boot-apache-xml
  [ext]=boot-ext [framework]=boot-framework [telephony-common]=boot-telephony-common
  [voip-common]=boot-voip-common [ims-common]=boot-ims-common
  [android.hidl.base-V1.0-java]=boot-android.hidl.base-V1.0-java
  [android.hidl.manager-V1.0-java]=boot-android.hidl.manager-V1.0-java
  [framework-oahl-backward-compatibility]=boot-framework-oahl-backward-compatibility
  [android.test.base]=boot-android.test.base
)
for jar in "${!MAP[@]}"; do
  v=${MAP[$jar]}
  dexpat="dex-std/${v}_classes*.dex"
  n=$(ls $dexpat 2>/dev/null | wc -l)
  if [ "$n" -eq 0 ]; then echo "!! $jar 无标准 dex"; continue; fi
  rm -rf /tmp/inj; mkdir -p /tmp/inj; i=1
  for f in $(ls $dexpat | sort); do
    if [ $i -eq 1 ]; then cp "$f" /tmp/inj/classes.dex; else cp "$f" /tmp/inj/classes$i.dex; fi
    i=$((i+1))
  done
  (cd /tmp/inj && zip -q -j $FW/$jar.jar classes*.dex)
  echo "INJ $jar ← $v ($n dex)"
done

echo '=== 验证（应为 6465780a 3033 39 00 = dex\n039）==='
cd /tmp && rm -rf mchk4 && mkdir mchk4 && cd mchk4
for j in core-oj core-libart framework ext; do
  unzip -o -q /root/x86guest/$FW/$j.jar classes.dex -d $j 2>/dev/null
  echo "$j: $(od -A n -t x1 -N 8 $j/classes.dex | tr -d ' ') $(stat -c%s $j/classes.dex)"
done
