#!/bin/bash
# §6.10 Step5P：下载 ART 9 源码（image_space.cc 等），定位 relocation/zygote 判定代码
cd /root/x86guest
mkdir -p artsrc && cd artsrc
TAG=android-9.0.0_r1
for f in runtime/gc/space/image_space.cc runtime/gc/space/image_space.h runtime/image.cc runtime/runtime.cc runtime/parsed_options.cc; do
  out=$(basename $f)
  [ -s "$out" ] && { echo "$out 已存在"; continue; }
  curl -sL --max-time 90 "https://raw.githubusercontent.com/aosp-mirror/platform_art/$TAG/$f" -o "$out"
  echo "$out: $(wc -c < "$out") 字节"
done
echo '=== "Only the zygote" 所在函数上下文 ==='
grep -n 'Only the zygote' *.cc | head -4
