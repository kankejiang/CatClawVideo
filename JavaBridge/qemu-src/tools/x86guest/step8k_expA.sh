#!/bin/bash
# §6.10 Step8k：expA（慢）与产品版（快）日志逐行对比 + 迅雷段执行迹象
cd /root/x86guest
echo '=== ① expA 里迅雷段/harness 迹象 ==='
grep -a 'thunder\|harness' expA.log | head -6
echo
echo '=== ② /harness 是否在 expA 包里 ==='
ls -la cmp/prod/harness 2>/dev/null || echo '（cmp/prod 无 /harness）'
echo
echo '=== ③ expA vs 产品版日志逐行 diff（去掉内核时间戳噪声）==='
sed 's/\[ *[0-9.]*\]//' expA.log > /tmp/expA_n.log
sed 's/\[ *[0-9.]*\]//' prod_boot.log > /tmp/prod_n.log
diff /tmp/prod_n.log /tmp/expA_n.log | head -50
echo
echo '=== ④ 行数 ==='
wc -l /tmp/prod_n.log /tmp/expA_n.log
