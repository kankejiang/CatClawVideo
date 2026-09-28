#!/bin/bash
# §6.10 Step6a：读 CanRelocate 实现 / Relocate 默认 / JNI 选项处理
cd /root/x86guest/artsrc
echo '=== runtime.cc 2445-2465（CanRelocate）==='
sed -n '2445,2465p' runtime.cc
echo
echo '=== runtime.cc 1150-1170（Start 里的赋值上下文）==='
sed -n '1150,1170p' runtime.cc
echo
echo '=== parsed_options.h 里 Relocate/Zygote 默认 ==='
grep -n -A2 -B2 'Relocate' parsed_options.h | head -20
echo
echo '=== java_vm_ext.cc 里 option 处理（前 30 条）==='
grep -n -i 'option' java_vm_ext.cc | head -30
