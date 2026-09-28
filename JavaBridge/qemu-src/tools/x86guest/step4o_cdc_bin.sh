#!/bin/bash
# §6.10 Step4o：解包 android_x86_64 版 compact_dex_converter 并验证可执行性
cd /root/x86guest/compact_dex_converter-master
cat README.md | head -20
cd /root/x86guest
mkdir -p cdc && cd cdc
unzip -o -q ../compact_dex_converter-master/compact_dex_converter_android_x86_64.zip
ls -la
find . -type f | head -10
for f in $(find . -type f -name '*converter*' -o -type f -name 'compact*'); do
  echo "--- $f ---"
  file "$f" 2>/dev/null || head -c 20 "$f" | od -A x -t x1z | head -2
done
