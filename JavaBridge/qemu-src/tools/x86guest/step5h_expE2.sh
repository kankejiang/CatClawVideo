#!/bin/bash
# §6.10 Step5h 实验 E2：注释 init 里 -Xnorelocate 导出（保留 locations），重打包启动
cd /root/x86guest
python3 - <<'PYEOF'
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
old = '''    export CATCLAW_JVM_EXTRA="-Xnorelocate${CATCLAW_JVM_EXTRA:+ $CATCLAW_JVM_EXTRA}"'''
new = '''    # 实验 E2：暂不导出 -Xnorelocate（它可能禁掉 in-place relocation 路线）'''
if old in s:
    s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print('已改 init：注释 -Xnorelocate 导出')
else:
    print('!! 未找到目标行，手动检查')
PYEOF
grep -n 'CATCLAW_JVM_EXTRA\|bootimg' art-tree/init | head -5
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪|bridge_exit'
echo '=== image 日志 ==='
grep -a 'Could not create image space\|image space\|relocat\|bootimg' boottest.log | head -8
