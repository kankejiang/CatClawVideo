#!/bin/bash
# §6.10 Step6h：读 DoParse 后半段 + -Xzygote/-Xrelocate 的完整 DSL 定义
cd /root/x86guest/artsrc
echo '=== DoParse 520-620 ==='
sed -n '520,620p' parsed_options.cc
echo
echo '=== -Xzygote 定义 ==='
grep -n -B3 -A5 '"-Xzygote"' parsed_options.cc | head -24
echo
echo '=== -Xrelocate/-Xnorelocate 定义（带上文）==='
grep -n -B6 -A4 '"-Xrelocate"' parsed_options.cc | head -24
