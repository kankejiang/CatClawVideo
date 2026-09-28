#!/bin/bash
# §6.10 Step8L：量化 expA 里 harness 重试次数与时间线
cd /root/x86guest
echo -n 'expA harness 退出次数: '; grep -ac 'harness 退出' expA.log
echo '=== expA 关键行序（行号:内容）==='
grep -an 'harness 监督循环已起\|bridge.GuestMain 监听\|harness 退出\|JNI_CreateJavaVM =' expA.log | head -16
echo
echo -n '产品版 harness 行数: '; grep -ac 'harness' prod_boot.log
echo -n '树版（boottest.log）harness 行数: '; grep -ac 'harness' boottest.log
echo '=== 树版 harness 迹象 ==='
grep -a 'harness' boottest.log | head -4
