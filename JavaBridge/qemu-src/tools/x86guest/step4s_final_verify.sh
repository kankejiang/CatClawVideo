#!/bin/bash
# §6.10 Step4s：15 jar 注入终验（magic 应全为 dex\n039）
cd /tmp && rm -rf mchk5 && mkdir mchk5 && cd mchk5
FW=/root/x86guest/art-tree/system/framework
ok=0; bad=0
for j in core-oj core-libart conscrypt okhttp bouncycastle apache-xml ext framework \
         telephony-common voip-common ims-common android.hidl.base-V1.0-java \
         android.hidl.manager-V1.0-java framework-oahl-backward-compatibility android.test.base; do
  unzip -o -q $FW/$j.jar classes.dex -d $j 2>/dev/null
  m=$(od -A n -t x1 -N 8 $j/classes.dex 2>/dev/null | tr -d ' ')
  if [ "${m:0:10}" = "6465780a30" ]; then ok=$((ok+1)); echo "OK  $j ($m, $(stat -c%s $j/classesdex 2>/dev/null || stat -c%s $j/classes.dex))";
  else bad=$((bad+1)); echo "BAD $j ($m)"; fi
done
echo "标准 dex: $ok  异常: $bad"
