#!/bin/bash
# §6.10 Step6S：B 轮（imageless）留存 + 与 A 轮的 dex2oat/日志对比
cd /root/x86guest
python3 - <<'PYEOF'
import re
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
s = re.sub(r'export CATCLAW_JVM_EXTRA="[^"]*"', 'export CATCLAW_JVM_EXTRA=""', s)
open(p, 'w', encoding='utf-8').write(s)
print('init extra = 空（对照）')
PYEOF

: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪'
cp boottest.log boottest_B.log

echo
echo '=== A 轮 vs B 轮 ==='
echo -n 'A: dex2oat took 次数 = '; grep -ac 'dex2oat took' boottest_A.log
grep -a 'dex2oat took' boottest_A.log | head -6
echo -n 'B: dex2oat took 次数 = '; grep -ac 'dex2oat took' boottest_B.log
grep -a 'dex2oat took' boottest_B.log | head -6
echo -n 'A 日志行数 = '; wc -l < boottest_A.log
echo -n 'B 日志行数 = '; wc -l < boottest_B.log
echo -n 'A image 回退 = '; grep -ac 'Could not create image space' boottest_A.log
echo -n 'B image 回退 = '; grep -ac 'Could not create image space' boottest_B.log
