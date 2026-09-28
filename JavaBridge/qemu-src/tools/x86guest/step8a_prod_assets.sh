#!/bin/bash
# §6.10 Step8a：产品化素材打包——修复版 artlaunch(x64) 编译 + bootimg/预置oat 打 tar
cd /root/x86guest
NDK=/opt/ndk/android-ndk-r27c/toolchains/llvm/prebuilt/linux-x86_64/bin

echo '=== ① 编修复版 artlaunch.x64 ==='
"$NDK/x86_64-linux-android28-clang" -O2 -Wl,--export-dynamic artlaunch_fixed.c -o artlaunch.x64.new -ldl
echo "exit=$?"
file artlaunch.x64.new | cut -c1-100

echo '=== ② bootimg 打 tar（45 文件）==='
( cd art-tree/system/framework/arm64 && tar -cf /root/x86guest/bootimg.tar $(ls boot*.art boot*.oat boot*.vdex) )
tar -tf /root/x86guest/bootimg.tar | wc -l

echo '=== ③ 预置 oat 打 tar（4 文件）==='
tar -C dalvik-preinit -cf /root/x86guest/preoat.tar arm64
tar -tf /root/x86guest/preoat.tar

echo '=== ④ gb.dex 一致性（预置 oat 绑定的 dex）==='
md5sum art-tree/gb.dex art-tree/tvbox.apk

echo '=== ⑤ 素材清单 ==='
ls -la bootimg.tar preoat.tar artlaunch.new artlaunch.x64.new art-tree/artlaunch
