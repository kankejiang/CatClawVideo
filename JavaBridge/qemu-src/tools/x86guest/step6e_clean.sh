#!/bin/bash
# §6.10 Step6e：清掉被 patch 过的 artsrc，全部强制重下干净源码
cd /root/x86guest
rm -rf artsrc && mkdir artsrc && cd artsrc
base='https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0'

fetch() {
  local f=$1; local out=$2
  for i in 1 2 3 4 5; do
    curl -sL --max-time 90 "$base/$f" -o "$out"
    sz=$(wc -c < "$out")
    [ "$sz" -gt 2000 ] && { echo "$out: $sz 字节（第 $i 次）"; return 0; }
    sleep 2
  done
  echo "$out 失败: $sz"
}

fetch 'runtime/gc/space/image_space.cc' image_space.cc
fetch 'runtime/runtime.cc' runtime.cc
fetch 'runtime/parsed_options.cc' parsed_options.cc
fetch 'runtime/java_vm_ext.cc' java_vm_ext.cc
fetch 'runtime/runtime.h' runtime.h

echo
echo '=== 调试痕迹检查（应无输出）==='
grep -n 'if (true' image_space.cc runtime.cc parsed_options.cc 2>/dev/null | head -5
echo '=== 干净版本重看 key 函数 ==='
grep -n 'bool Runtime::Create' runtime.cc
grep -n 'is_zygote_ = \|must_relocate_ = ' runtime.cc | head -6
grep -n 'bool Runtime::CanRelocate' runtime.cc
