#!/bin/bash
# §6.10 Step5W 实验 E5：init 导出 -Xnorelocate，验证 artlaunch 是否附加 + image 结果
cd /root/x86guest
python3 - <<'PYEOF'
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
old = '''    # 实验 E3：-Xzygote 放行「重定位到 /data/dalvik-cache」分支（非 zygote 时该分支被拒）
    export CATCLAW_JVM_EXTRA="-Xzygote${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"'''
new = '''    # 实验 E5：-Xnorelocate（ShouldRelocate=false → Step 2.a 原位加载分支）
    export CATCLAW_JVM_EXTRA="-Xnorelocate${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"'''
if old in s:
    s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print('已改 init：导出 -Xnorelocate')
else:
    print('!! 未找到 E3 标记行；当前相关行：')
PYEOF
grep -n 'CATCLAW_JVM_EXTRA' art-tree/init | head -3

# 下载 runtime 源码（并行第一条）
cd /root/x86guest/artsrc
for f in runtime/runtime.h runtime/runtime.cc; do
  out=$(basename $f)
  [ -s "$out" ] || curl -sL --max-time 70 "https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0/$f" -o "$out"
  echo "$out: $(wc -c < $out)"
done

# 重跑启动
cd /root/x86guest
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪|bridge_exit'
echo '=== artlaunch 附加选项（应有 -Xnorelocate）==='
grep -a '附加 JVM 选项' boottest.log | head -5
echo '=== image 结果 ==='
grep -a 'Could not create image space\|image space\|relocat' boottest.log | head -4
