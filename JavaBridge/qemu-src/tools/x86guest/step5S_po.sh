#!/bin/bash
# §6.10 Step5S：下载 parsed_options.cc / runtime.cc，查 -Xnorelocate 拼写与 ShouldRelocate 定义
cd /root/x86guest/artsrc
TAG=lineage-16.0
for f in runtime/parsed_options.cc; do
  out=$(basename $f)
  [ -s "$out" ] || curl -sL --max-time 70 "https://raw.githubusercontent.com/LineageOS/android_art/$TAG/$f" -o "$out"
  echo "$out: $(wc -c < $out)"
done
echo '=== relocate 相关选项 ==='
grep -n -i 'relocat' parsed_options.cc | head -20
echo '=== image_relocation_ 定义与默认 ==='
grep -n 'image_relocation' parsed_options.cc | head -10
