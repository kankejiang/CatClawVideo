#!/bin/bash
# §6.10 Step5T：列 runtime 目录 + 下载选项解析文件 + 查 relocate 拼写/默认
cd /root/x86guest/artsrc
echo '=== runtime 目录（含 option 的文件）==='
curl -sL --max-time 50 'https://api.github.com/repos/LineageOS/android_art/contents/runtime?ref=lineage-16.0' \
  | grep -oE '"name": *"[^"]*"' | grep -iE 'option|runtime|android' | head -20

for f in runtime/parsed_options.cc runtime/parsed_options.h; do
  out=$(basename $f)
  [ -s "$out" ] || curl -sL --max-time 70 "https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0/$f" -o "$out"
  echo "$out: $(wc -c < $out)"
done

if [ "$(wc -c < parsed_options.cc)" -gt 1000 ]; then
  echo '=== relocate 相关行 ==='
  grep -n -i 'relocat' parsed_options.cc | head -14
fi
