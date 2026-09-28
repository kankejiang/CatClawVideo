#!/bin/bash
# §6.10 Step5Z：重试下载 runtime.cc / java_vm_ext.cc（raw 偶发 404）
cd /root/x86guest/artsrc
base='https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0'

fetch_retry() {
  local f=$1; local out=$2
  for i in 1 2 3 4 5; do
    curl -sL --max-time 60 "$base/$f" -o "$out"
    sz=$(wc -c < "$out")
    if [ "$sz" -gt 5000 ]; then echo "$out 成功: $sz 字节（第 $i 次）"; return 0; fi
    sleep 3
  done
  echo "$out 失败（最后 $sz 字节）"; return 1
}

fetch_retry 'runtime/runtime.cc' runtime.cc
fetch_retry 'runtime/java_vm_ext.cc' java_vm_ext.cc

echo '=== runtime.cc: must_relocate_ / is_zygote_ / CanRelocate ==='
grep -n 'must_relocate_\|is_zygote_\|CanRelocate' runtime.cc 2>/dev/null | head -16
echo '=== java_vm_ext.cc: sanitize/选项过滤 ==='
grep -n 'Xnorelocate\|Xzygote\|Sanitize\|Ignoring' java_vm_ext.cc 2>/dev/null | head -12
