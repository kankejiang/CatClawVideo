#!/bin/sh
# 重编 guest 侧的三个原生件（artlaunch / proppreload.so / fakelogd）。
# 用法：cd D:/Code/.shot && sh v1/mk.sh
set -e
NDK="/c/Users/lvjin/AppData/Local/Android/Sdk/ndk/27.0.12077973/toolchains/llvm/prebuilt/windows-x86_64"
CC="$NDK/bin/clang.exe"
T="--target=aarch64-linux-android28"
cd "$(dirname "$0")"
# ⚠ 两个 TU 一起编：dnsshim.c 是 guest 里唯一的域名解析通路（本 guest 没有 netd，
# bionic 自己一台 DNS 都拿不到）。2026-09-26 只编 proppreload.c 重打了一次 .so，
# 垫片被静默丢掉，症状是「荐片 load 里 Gson 拿到 null」——查起来像站点问题。
"$CC" $T -O2 -fPIC -shared proppreload.c dnsshim.c -o proppreload.so
"$CC" $T -O2 -Wl,--export-dynamic artlaunch.c -o artlaunch -ldl
"$CC" $T -O2 fakelogd.c -o fakelogd
ls -la proppreload.so artlaunch fakelogd
echo OK
