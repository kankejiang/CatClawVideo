#!/bin/bash
# §6.10 Step6c：读 Runtime::Create → ParsedOptions::Create 的路径与忽略逻辑
cd /root/x86guest/artsrc
echo '=== runtime.cc: Runtime::Create ==='
grep -n 'bool Runtime::Create' runtime.cc
sed -n "$(grep -n 'bool Runtime::Create' runtime.cc | head -1 | cut -d: -f1),+30p" runtime.cc
echo
echo '=== parsed_options.cc: 忽略/未识别逻辑 ==='
grep -n -i 'ignor\|unrecognized\|unknown' parsed_options.cc | head -12
