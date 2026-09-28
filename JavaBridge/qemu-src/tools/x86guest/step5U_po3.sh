#!/bin/bash
# §6.10 Step5U：parsed_options.h 查 ShouldRelocate + 重试 .cc
cd /root/x86guest/artsrc
echo '=== parsed_options.h 里 relocate ==='
grep -n -i 'relocat' parsed_options.h | head -10
echo
echo '=== 重试 parsed_options.cc ==='
curl -sL --max-time 90 "https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0/runtime/parsed_options.cc" -o parsed_options.cc
echo "大小: $(wc -c < parsed_options.cc)"
if [ "$(wc -c < parsed_options.cc)" -gt 1000 ]; then
  grep -n -i 'relocat' parsed_options.cc | head -14
fi
echo
echo '=== image_space.cc 里 ShouldRelocate 的引用 ==='
grep -n 'ShouldRelocate\|image_relocation' /root/x86guest/artsrc/image_space.cc | head -6
