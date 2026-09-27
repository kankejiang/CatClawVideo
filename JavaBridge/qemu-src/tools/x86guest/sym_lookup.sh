#!/bin/bash
# 解析 libart 里 native_bridge_art_callbacks_ 各函数指针的符号名（readelf 精确匹配）
cd /root/x86guest || exit 1
readelf -sW rootfs/apex/com.android.art/lib64/libart.so > /tmp/artsyms.txt
for a in 71cdc0 71d540 71dc30 d077d b867a 71f0e0 9e64b 9b23c 71f9d0; do
    hit=$(grep -E " $a[[:space:]]" /tmp/artsyms.txt | head -1 | sed 's/^.*[[:space:]]//')
    printf "0x%-8s -> %s\n" "$a" "$hit"
done
