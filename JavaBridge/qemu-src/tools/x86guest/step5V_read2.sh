#!/bin/bash
# §6.10 Step5V：读 -Xrelocate 定义（默认值）与 image_space.cc 的两个判定点
cd /root/x86guest/artsrc
echo '=== parsed_options.cc 210-232（-Xrelocate 定义）==='
sed -n '210,232p' parsed_options.cc
echo
echo '=== parsed_options.cc 里 Relocate 字段的类型/默认 ==='
grep -n 'Relocate' parsed_options.cc | head -10
echo
echo '=== image_space.cc 315-345（Loader 里的 ShouldRelocate）==='
sed -n '315,345p' image_space.cc
