#!/bin/bash
# §6.10 Step6R：A 轮（-Xnorelocate）日志取证——12s 变慢花在哪
cd /root/x86guest
python3 - <<'PYEOF'
import re
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
s = re.sub(r'export CATCLAW_JVM_EXTRA="[^"]*"', 'export CATCLAW_JVM_EXTRA="-Xnorelocate"', s)
open(p, 'w', encoding='utf-8').write(s)
print('init extra = -Xnorelocate')
PYEOF

: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪'
cp boottest.log boottest_A.log

echo '=== A 轮：boot.art/image/relocat 相关行 ==='
grep -a 'boot\.art\|image\|relocat\|ImageSpace\|oat' boottest_A.log | grep -av '附加 JVM' | head -20
echo
echo '=== A 轮：WARNING/ERROR 行统计 ==='
grep -ac 'W art\|E art\|WARNING\|ERROR' boottest_A.log
echo '=== A 轮日志总行数 ==='
wc -l boottest_A.log
echo '=== A 轮：guest 时间线（init 各阶段时间戳）==='
grep -a 'User time\|art guest begin\|bridge\|GuestMain\|等 accept' boottest_A.log | head -10
