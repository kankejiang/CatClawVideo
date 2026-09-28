#!/bin/bash
# §6.10 Step8h：init 全文 diff（无过滤）+ jar 的 dex 内容对比
cd /root/x86guest
echo '=== ① init 完整 diff（全 >300 行，只列 < 与 > 的差异段）==='
diff cmp/tree/init cmp/prod/init | head -120
echo
echo '=== ② core-oj.jar 内部结构 ==='
echo '-- tree --'
unzip -l cmp/tree/system/framework/core-oj.jar 2>/dev/null | head -6
echo '-- prod --'
unzip -l cmp/prod/system/framework/core-oj.jar 2>/dev/null | head -6
echo
echo '=== ③ jar 内 dex 的 md5（内容是否一致）==='
for d in tree prod; do
  echo "-- $d --"
  unzip -p cmp/$d/system/framework/core-oj.jar 2>/dev/null | md5sum
  unzip -l cmp/$d/system/framework/core-oj.jar 2>/dev/null | grep -c '\.dex'
done
echo
echo '=== ④ framework.jar 大小对比 ==='
ls -la cmp/tree/system/framework/framework.jar cmp/prod/system/framework/framework.jar cmp/tree/system/framework/core-oj.jar cmp/prod/system/framework/core-oj.jar
