#!/bin/bash
# §6.10 Step5g：boot.art 的 ELF 结构 + relocation 报错上下文
A=/root/x86guest/art-tree/system/framework/boot.art
echo '=== file ==='
file "$A"
echo '=== ELF 头 ==='
readelf -h "$A" 2>/dev/null | grep -E 'Type|Machine|Entry'
echo '=== 段表（找 rel） ==='
readelf -S "$A" 2>/dev/null | grep -iE 'rel|Name|\.art' | head -12
echo '=== boot.art 内嵌首字符串（oat 头信息） ==='
strings -n 8 "$A" 2>/dev/null | grep -aiE 'multi-image|Xnorelocate|image' | head -6
echo '=== boottest.log relocation 上下文 ==='
grep -a -B2 -A2 'Cannot relocate image' /root/x86guest/boottest.log | head -14
