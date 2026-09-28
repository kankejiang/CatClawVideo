#!/bin/bash
# §6.10 Step5k：boot.oat 里记录的编译元数据 + boot.art 头 hexdump
A=/root/x86guest/art-tree/system/framework/arm64
echo '=== boot.oat 编译元数据 strings ==='
strings -n 6 $A/boot.oat | grep -iE 'norelocate|multi-image|compile-pic|image|target|base' | head -20
echo
echo '=== boot.art 头 128 字节 ==='
xxd -l 128 $A/boot.art
echo
echo '=== boot.art 尾（rel 段迹象：文件尾是否有 ELF rel）==='
tail -c 64 $A/boot.art | xxd
