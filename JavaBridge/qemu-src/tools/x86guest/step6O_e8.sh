#!/bin/bash
# §6.10 Step6O 实验 E8：-Xuse-stderr-logger + -verbose:startup（日志直出 serial）
cd /root/x86guest
python3 - <<'PYEOF'
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
old = '''    # 实验 E7：-verbose:startup 探针（验证 JNI 选项通道）+ -Xnorelocate
    export CATCLAW_JVM_EXTRA="-Xnorelocate -verbose:startup"'''
new = '''    # 实验 E8：stderr logger 直出 + startup 日志 + -Xnorelocate
    export CATCLAW_JVM_EXTRA="-Xnorelocate -verbose:startup -Xuse-stderr-logger"'''
if old in s:
    s = s.replace(old, new)
    open(p, 'w', encoding='utf-8').write(s)
    print('已改 init（E8）')
else:
    print('!! 未找到 E7 标记行')
PYEOF

# 并行：下载 cmdline.h 看多名字选项语义
cd /root/x86guest/artsrc
for i in 1 2 3; do
  curl -sL --max-time 60 "https://raw.githubusercontent.com/LineageOS/android_art/lineage-16.0/cmdline/cmdline.h" -o cmdline.h
  [ "$(wc -c < cmdline.h)" -gt 5000 ] && break; sleep 2
done
echo "cmdline.h: $(wc -c < cmdline.h) 字节"

cd /root/x86guest
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪' 
echo '=== stderr 直出证据（无 [logd] 前缀的 art 行）==='
grep -a 'Runtime::' boottest.log | head -6
echo '=== -verbose:startup 生效证据 ==='
grep -a 'startup entering\|Start entering\|verbose:startup' boottest.log | head -4
echo '=== image 报错 ==='
grep -a 'Could not create image space' boottest.log | head -1 | cut -c1-200
