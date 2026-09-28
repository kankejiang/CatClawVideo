#!/bin/bash
# §6.10 Step6k 实验 E7：-verbose:startup 验证 JNI 选项通道是否有效
#   预期证据：runtime.cc 有 VLOG(startup) << "Runtime::Init -verbose:startup enabled"
cd /root/x86guest
python3 - <<'PYEOF'
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
old = '''    # 实验 E5：-Xnorelocate（ShouldRelocate=false → Step 2.a 原位加载分支）
    export CATCLAW_JVM_EXTRA="-Xnorelocate${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"'''
new = '''    # 实验 E7：-verbose:startup 探针（验证 JNI 选项通道）+ -Xnorelocate
    export CATCLAW_JVM_EXTRA="-Xnorelocate -verbose:startup"'''
if old in s:
    s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print('已改 init：extra = -Xnorelocate -verbose:startup')
else:
    print('!! 未找到 E5 标记行')
PYEOF
grep -n 'CATCLAW_JVM_EXTRA' art-tree/init | head -2

: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪|bridge_exit'
echo '=== artlaunch 附加选项 ==='
grep -a '附加 JVM 选项' boottest.log | head -4
echo '=== startup 探针证据（应出现）==='
grep -a 'verbose:startup\|startup enabled\|Verbose' boottest.log | head -4
echo '=== image 报错（关注是否变化）==='
grep -a 'Could not create image space' boottest.log | head -1 | cut -c1-220
