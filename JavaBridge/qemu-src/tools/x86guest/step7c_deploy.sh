#!/bin/bash
# §6.10 Step7c：预置 oat 部署进 art-tree/data/dalvik-cache/arm64 + 重打包启动验证
cd /root/x86guest
TREE=/root/x86guest/art-tree
OUT=/root/x86guest/dalvik-preinit/arm64

# 等 tvbox 预编收尾（最多 3 分钟）
for i in $(seq 1 18); do
  pgrep -f 'dex2oatd.*tvbox' >/dev/null 2>&1 || break
  sleep 10
done
echo "=== 预编后的缓存文件 ==="
ls -la "$OUT/"

echo '=== 部署到 art-tree 的 /data/dalvik-cache/arm64 ==='
mkdir -p "$TREE/data/dalvik-cache/arm64"
cp -f "$OUT/gb.dex" "$OUT/gb.vdex" "$OUT/tvbox.apk@classes.dex" "$OUT/tvbox.apk@classes.vdex" "$TREE/data/dalvik-cache/arm64/"
ls -la "$TREE/data/dalvik-cache/arm64/"

echo '=== init extra 设回 -Xnorelocate ==='
python3 - <<'PYEOF'
import re
p = '/root/x86guest/art-tree/init'
s = open(p, encoding='utf-8').read()
s = re.sub(r'export CATCLAW_JVM_EXTRA="[^"]*"', 'export CATCLAW_JVM_EXTRA="-Xnorelocate"', s)
open(p, 'w', encoding='utf-8').write(s)
print('extra = -Xnorelocate')
PYEOF

echo '=== 重打包 + 冷启动（预置 oat 版）==='
: > /tmp/fakelogd.log
bash /root/x86guest/step5_boot_test.sh 2>&1 | grep -aE '桥就绪'
cp boottest.log boottest_preoat.log
echo -n 'dex2oat 相关行数（期待 0）：'; grep -ac 'dex2oat' boottest_preoat.log
echo -n 'image 回退（期待 0）：'; grep -ac 'Could not create image space' boottest_preoat.log
grep -a 'dex2oat took' boottest_preoat.log | head -3
