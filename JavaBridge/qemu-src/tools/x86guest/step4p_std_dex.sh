#!/bin/bash
# §6.10 Step4p：45 件 cdex → 标准 dex → 塞回 jar（zip 替换）
set -x
cd /root/x86guest
mkdir -p dex-std; rm -f dex-std/*.dex

FAIL=0
for f in dex-out/*.cdex; do
  b=$(basename "$f" .cdex)
  cp "$f" /tmp/work.cdex
  if ! ./cdc/compact_dex_converter /tmp/work.cdex > /dev/null 2>&1; then
    echo "CONVERT_FAIL $b"; FAIL=1; continue
  fi
  m=$(od -A n -t x1 -N 8 /tmp/work.cdex.new | tr -d ' ')
  # dex\n035/037/039（039 = Android 9 标准，转换器按源码版本输出）
  case "$m" in
    6465780a30333500|6465780a30333700|6465780a30333900) ;;
    *) echo "BAD_MAGIC $b: $m"; FAIL=1; continue ;;
  esac
  mv /tmp/work.cdex.new dex-std/$b.dex
  echo "OK $b $(stat -c%s dex-std/$b.dex)"
done
[ $FAIL -eq 1 ] && { echo '!! 有转换失败，中止'; exit 1; }
echo "标准 dex 共 $(ls dex-std/*.dex | wc -l) 件，合计 $(du -ch dex-std/*.dex | tail -1 | cut -f1)"

# ── 塞回 jar（zip 替换：dex-std/boot_classes.dex → core-oj.jar:/classes.dex 等）──
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
FW=art-tree/system/framework
for jar in "${!MAP[@]}"; do
  v=${MAP[$jar]}
  rm -rf /tmp/inj; mkdir -p /tmp/inj; i=1
  for f in $(ls dex-std/${v}_classes*.dex | sort); do
    if [ $i -eq 1 ]; then cp "$f" /tmp/inj/classes.dex; else cp "$f" /tmp/inj/classes$i.dex; fi
    i=$((i+1))
  done
  (cd /tmp/inj && zip -q -j $FW/$jar.jar classes*.dex)
  echo "jar: $jar ← $v ($(du -h $FW/$jar.jar | cut -f1))"
done

# magic 抽查
cd /tmp && rm -rf mchk2 && mkdir mchk2 && cd mchk2 && unzip -o -q $FW/core-oj.jar classes.dex && od -A x -t x1z -v classes.dex | head -1
