#!/bin/bash
# §6.10 Step6j：侦察 libartd.so 的 patch 内容 + 交叉工具链
LIB=/root/x86guest/art-tree/system/lib64/libartd.so
echo '=== libartd.so 调试串（option 相关）==='
strings -n 4 "$LIB" | grep -a 'option' | head -14
echo
echo '=== Xzygote/relocate 相关串 ==='
strings "$LIB" | grep -aE 'Xzygote|norelocate' | head -10
echo
echo '=== 108 交叉编译器 ==='
which aarch64-linux-gnu-gcc 2>/dev/null || echo '无 aarch64-linux-gnu-gcc'
ls /usr/bin 2>/dev/null | grep -i aarch64 | head -5
echo
echo '=== 时间戳对照 ==='
ls -la /root/x86guest/art-tree/artlaunch "$LIB" 2>/dev/null
