#!/bin/bash
# §6.10 Step5R：换镜像源下载 ART 9 image_space.cc
cd /root/x86guest/artsrc
F='runtime/gc/space/image_space.cc'

echo '== 候选 1：LineageOS android_art（lineage-16.0 = P）=='
code=$(curl -sL -o t1.cc -w '%{http_code}' --max-time 60 "https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0/$F")
echo "HTTP $code, $(wc -c < t1.cc) 字节"
if [ "$(wc -c < t1.cc)" -gt 10000 ]; then cp t1.cc image_space.cc; fi

if [ ! -s image_space.cc ]; then
  echo '== 候选 2：android.googlesource.com（base64）=='
  code=$(curl -sL -o t2.b64 -w '%{http_code}' --max-time 90 "https://android.googlesource.com/platform/art/+/android-9.0.0_r1/$F?format=TEXT")
  echo "HTTP $code, $(wc -c < t2.b64) 字节"
  base64 -d t2.b64 > image_space.cc 2>/dev/null && echo "解码后 $(wc -c < image_space.cc) 字节"
fi

if [ -s image_space.cc ]; then
  echo '=== "Only the zygote" 上下文 ==='
  grep -n 'Only the zygote' image_space.cc | head -3
  echo '=== "Cannot relocate image" 上下文 ==='
  grep -n 'Cannot relocate image' image_space.cc | head -3
fi
