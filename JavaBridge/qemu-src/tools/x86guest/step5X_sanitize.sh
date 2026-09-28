#!/bin/bash
# §6.10 Step5X：证实 JNI 选项净化 + 下载 runtime.cc（jsdelivr）
echo '=== boottest.log 里的选项相关日志 ==='
grep -aiE 'ignoring option|unknown option|sanitiz|Unrecognized' /root/x86guest/boottest.log | head -8
echo '=== fakelogd.log 同上 ==='
grep -aiE 'ignoring option|unknown option|sanitiz' /tmp/fakelogd.log | head -8
echo
echo '=== jsdelivr 下载 runtime.cc ==='
cd /root/x86guest/artsrc
curl -sL --max-time 90 "https://cdn.jsdelivr.net/gh/LineageOS/android_art@lineage-16.0/runtime/runtime.cc" -o runtime.cc
echo "runtime.cc: $(wc -c < runtime.cc)"
if [ "$(wc -c < runtime.cc)" -gt 10000 ]; then
  echo '=== CanRelocate / must_relocate_ / is_zygote_ 初始化 ==='
  grep -n 'CanRelocate\|must_relocate_\|is_zygote_' runtime.cc | head -14
fi
