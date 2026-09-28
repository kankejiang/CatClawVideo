#!/bin/bash
# §6.10 Step5Y：googlesource 下 runtime.cc/java_vm_ext.cc，定位选项净化与 must_relocate_/is_zygote_ 初始化
cd /root/x86guest/artsrc
GS='https://android.googlesource.com/platform/art/+/android-9.0.0_r1'
for f in runtime/java_vm_ext.cc runtime/runtime.cc; do
  out=$(basename $f)
  [ -s "$out" ] || {
    curl -sL --max-time 120 "$GS/$f?format=TEXT" -o /tmp/b64.$$
    base64 -d /tmp/b64.$$ > "$out" 2>/dev/null
  }
  echo "$out: $(wc -c < $out)"
done
echo '=== java_vm_ext.cc 的 sanitize 与 -Xzygote/-Xnorelocate ==='
grep -n 'Xnorelocate\|Xzygote\|Sanitize\|Ignoring option' java_vm_ext.cc 2>/dev/null | head -12
echo '=== runtime.cc 的 must_relocate_ / is_zygote_ / CanRelocate ==='
grep -n 'must_relocate_\|is_zygote_\|CanRelocate' runtime.cc 2>/dev/null | head -14
