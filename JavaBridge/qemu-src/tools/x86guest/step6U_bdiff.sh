#!/bin/bash
# §6.10 Step6U：A/B 轮 dexopt 行为差异定位
cd /root/x86guest
echo -n 'A dex2oat 行数: '; grep -ac 'dex2oat' boottest_A.log
echo -n 'B dex2oat 行数: '; grep -ac 'dex2oat' boottest_B.log
echo
echo '=== B 轮里的 classpath/oat 相关行 ==='
grep -a 'ClassLoaderContext\|dalvik-cache\|OatFile\|oat\|OpenDex\|DexClassLoader' boottest_B.log | head -10
echo
echo '=== A 轮里对应的行 ==='
grep -a 'ClassLoaderContext\|dalvik-cache\|OatFile\|OpenDex\|DexClassLoader' boottest_A.log | head -10
echo
echo '=== [guest] 行差异 ==='
diff <(grep -a '^\[guest\]' boottest_A.log) <(grep -a '^\[guest\]' boottest_B.log) | head -12
