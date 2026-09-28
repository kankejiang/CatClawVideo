#!/bin/bash
# §6.10 Step6T：A 轮 dex2oat 调用上下文（谁触发、在时间线哪里）
cd /root/x86guest
echo '=== 14.25s dex2oat 的触发命令行与其前 15 行 ==='
grep -an 'dex2oat --dex-file' boottest_A.log | head -6
echo '---'
n=$(grep -an 'dex2oat --dex-file=/tvbox.apk' boottest_A.log | head -1 | cut -d: -f1)
[ -n "$n" ] && sed -n "$((n>15?n-15:1)),$((n+3))p" boottest_A.log
echo
echo '=== 桥启动关键行（对照时间线）==='
grep -an 'GuestMain 监听\|等 accept\|dex2oat took\|JNI_CreateJavaVM' boottest_A.log | head -12
