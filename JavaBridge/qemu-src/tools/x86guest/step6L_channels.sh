#!/bin/bash
# §6.10 Step6L：找「旁路通道」——VLOG(startup) 输出点 / dalvik.vm 属性 / getenv 兜底
cd /root/x86guest/artsrc
echo '=== VLOG(startup) 出现点（-verbose:startup 应有输出）==='
grep -n 'VLOG(startup)' runtime.cc | head -8
echo
echo '=== runtime.cc / parsed_options.cc 里 dalvik.vm 属性 ==='
grep -n 'dalvik.vm' runtime.cc parsed_options.cc 2>/dev/null | head -12
echo
echo '=== parsed_options.cc 里的 getenv 通道 ==='
grep -n 'getenv' parsed_options.cc | head -12
echo
echo '=== java_vm_ext.cc 里 CreateJavaVM 是否过滤选项 ==='
grep -n 'IsRuntimeOnly\|runtime-only\|RuntimeOnly\|CanBeUsedWithJni\|JNI' java_vm_ext.cc | head -14
