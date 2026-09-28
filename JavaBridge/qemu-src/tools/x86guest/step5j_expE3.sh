#!/bin/bash
# §6.10 Step5j 实验 E3：init 导出 -Xzygote（非 -Xnorelocate），放行 relocate-to-dalvik-cache 分支
cd /root/x86guest
python3 - <<'PYEOF'
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
old = '''    # 实验 E2：暂不导出 -Xnorelocate（它可能禁掉 in-place relocation 路线）'''
new = '''    # 实验 E3：-Xzygote 放行「重定位到 /data/dalvik-cache」分支（非 zygote 时该分支被拒）
    export CATCLAW_JVM_EXTRA="-Xzygote${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"'''
if old in s:
    s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print('已改 init：导出 -Xzygote')
else:
    print('!! 未找到 E2 标记行，当前 init 相关行：')
PYEOF
grep -n 'CATCLAW_JVM_EXTRA\|zygote' art-tree/init | head -4
: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪|bridge_exit'
echo '=== image 日志 ==='
grep -aiE 'image space|relocat|dalvik-cache|bootimg|zygote' /root/x86guest/boottest.log | head -8
echo '=== /data/dalvik-cache 产物 ==='
ls -la art-tree/data/dalvik-cache/arm64/ 2>/dev/null | head -6
