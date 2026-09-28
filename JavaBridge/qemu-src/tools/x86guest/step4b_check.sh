#!/bin/bash
# §6.10 Step4b：确认 15 项 BCP 的 dex 抽取完整性 + preloaded-classes 清单
cd /root/x86guest
echo '=== 15 项 BCP 的 dex 完整性 ==='
for n in boot boot-core-libart boot-conscrypt boot-okhttp boot-bouncycastle \
         boot-apache-xml boot-ext boot-framework boot-telephony-common \
         boot-voip-common boot-ims-common boot-android.hidl.base-V1.0-java \
         boot-android.hidl.manager-V1.0-java boot-framework-oahl-backward-compatibility \
         boot-android.test.base; do
  c=$(ls dex-out/${n}_classes*.cdex 2>/dev/null | wc -l)
  s=$(du -ch dex-out/${n}_classes*.cdex 2>/dev/null | tail -1 | cut -f1)
  echo "$n: ${c} 个dex  $s"
done
echo '=== preloaded-classes ==='
ls -la art-tree/system/etc/preloaded-classes 2>/dev/null || find art-tree/system -name 'preloaded-classes' 2>/dev/null | head -3
echo '=== dex2oatd 支持的 compiler-filter 抽查（--help 里 verify/quicken）==='
/usr/bin/qemu-aarch64-static -L art-tree ./dex2oatd --help 2>&1 | grep -o 'verify\|quicken\|speed-profile' | sort -u | head
