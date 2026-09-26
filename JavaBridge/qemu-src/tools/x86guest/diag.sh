#!/bin/bash
# x86 guest 启动诊断：初始化序列 + abort 现场 + linkerconfig 生成物
cd /root/x86guest || exit 1
echo '==== 初始化序列 ===='
grep -nE 'artlaunch:|nativeloader|\[init\]|ld.config|JNI_CreateJavaVM = |boot image|Boot image' qemu-boot.log | head -30
echo '==== Fatal signal 前后 ===='
n=$(grep -n 'Fatal signal' qemu-boot.log | head -1 | cut -d: -f1)
if [ -n "$n" ]; then sed -n "$((n-25)),$((n+2))p" qemu-boot.log | cut -c1-190; fi
echo '==== initrd 内 linkerconfig 生成物 ===='
zcat art_initrd_x64.gz | cpio -it 2>/dev/null | grep linkerconfig
