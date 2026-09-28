#!/bin/bash
# §6.10 Step6M：读 CreateJavaVM 全函数（options 的真实去向）
cd /root/x86guest/artsrc
echo '=== 函数入口定位 ==='
grep -n 'JNI_CreateJavaVM\|CreateJavaVM(' java_vm_ext.cc | head -6
echo
echo '=== CreateJavaVM 主体（定位后 100 行）==='
start=$(grep -n 'extern "C" jint JNI_CreateJavaVM\|jint JNI_CreateJavaVM' java_vm_ext.cc | head -1 | cut -d: -f1)
echo "（起点 $start）"
sed -n "$((start)),$((start+95))p" java_vm_ext.cc
