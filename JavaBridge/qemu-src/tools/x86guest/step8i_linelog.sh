#!/bin/bash
# §6.10 Step8i：日志逐行 diff + init 关键行 grep + JIT 计数
cd /root/x86guest
echo '=== ① 日志逐行 diff（prod vs tree，前 60 行差异）==='
diff prod_boot.log boottest.log | head -60
echo
echo '=== ② init 关键行 ==='
for f in cmp/tree/init cmp/prod/init; do
  echo "-- $f --"
  grep -n 'boot.art \]' "$f" | head -3
  grep -n 'CATCLAW_BCP_LOCATIONS\|CATCLAW_JVM_EXTRA' "$f" | head -4
done
echo
echo '=== ③ JIT/编译相关计数 ==='
echo -n 'prod: '; grep -aci 'jit\|compil' prod_boot.log
echo -n 'tree: '; grep -aci 'jit\|compil' boottest.log
