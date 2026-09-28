#!/bin/bash
# §6.10 Step8f：两版启动日志 diff——慢的那版多做了什么
cd /root/x86guest
echo "行数: prod=$(wc -l < prod_boot.log) tree=$(wc -l < boottest.log)"
echo
echo '=== 归一化后的差异行（tree 独有 <，prod 独有 >）==='
grep -a '\[art\]\|\[guest\]\|\[bootimg\]\|\[proppreload\]\|User time\|bridge' boottest.log | sed -E 's/[0-9]+//g' | sort -u > /tmp/lt.txt
grep -a '\[art\]\|\[guest\]\|\[bootimg\]\|\[proppreload\]\|User time\|bridge' prod_boot.log | sed -E 's/[0-9]+//g' | sort -u > /tmp/lp.txt
diff /tmp/lt.txt /tmp/lp.txt | head -40
echo
echo '=== 慢版（tree）里 ART 阶段的日志尾部 20 行 ==='
grep -an '\[art\]\|\[guest\]' boottest.log | tail -12
