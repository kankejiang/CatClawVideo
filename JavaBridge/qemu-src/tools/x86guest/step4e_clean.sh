#!/bin/bash
# §6.10 Step4e：清掉 arm64/ 下的旧镜像分件（上一轮 mv 的 glob 只清了 framework/ 根）
pkill -f dex2oatd 2>/dev/null
sleep 1
echo '=== 杀后 dex2oatd 进程数 ==='
pgrep -cf dex2oatd || true
cd /root/x86guest
echo '=== arm64/ 残留 ==='
ls art-tree/system/framework/arm64/ | head -6
mv art-tree/system/framework/arm64/boot* boot-backup/ 2>/dev/null
echo '=== 清理后 arm64/ ==='
ls -la art-tree/system/framework/arm64/
echo '=== framework/ 根的 boot 残留（应为空）==='
ls art-tree/system/framework/boot* 2>/dev/null || echo '（无）'
echo OK
