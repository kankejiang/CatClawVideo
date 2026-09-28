#!/bin/bash
# §6.10 Step6P：编译修复版 artlaunch（选项注入顺序 bug）+ 部署 + 启动验证一条龙
set -e
cd /root/x86guest
NDK=/opt/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64/bin
CC="$NDK/aarch64-linux-android28-clang"

echo '=== ① 编译 ==='
"$CC" -O2 -Wl,--export-dynamic artlaunch_fixed.c -o artlaunch.new -ldl
echo "compile OK: $(ls -la artlaunch.new | awk '{print $5}') 字节"
file artlaunch.new | cut -c1-110

echo '=== ② 部署（旧版备份 artlaunch.bak-optseq）==='
cp -f art-tree/artlaunch art-tree/artlaunch.bak-optseq
cp -f artlaunch.new art-tree/artlaunch

echo '=== ③ 重打包 + 冷启动 ==='
: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪|bridge_exit'

echo '=== ④ 附加选项（从此刻起 ART 真能收到）==='
grep -a '附加 JVM 选项' boottest.log | head -5
echo '=== ⑤ -verbose:startup 生效证据（应出现 startup 日志）==='
grep -a 'Start entering\|startup enabled\|verbose:startup' boottest.log | head -6
echo '=== ⑥ image 结果（应变化！）==='
grep -a 'Could not create image space\|image space\|relocat\|dlopen' boottest.log | head -5
