#!/bin/bash
# §6.10 Step6b：查 RuntimeOnly 机制（哪些选项 JNI 不能传）+ dalvik.vm 属性输入源
cd /root/x86guest/artsrc
echo '=== parsed_options.cc: RuntimeOnly 定义与检查 ==='
grep -n -A3 -B3 'RuntimeOnly' parsed_options.cc | head -40
echo
echo '=== runtime-only 相关/Property 输入源 ==='
grep -n 'dalvik.vm\|runtime-only\|RuntimeOnly' parsed_options.cc | head -20
