#!/bin/bash
# §6.10 Step2：108 上准备 dex2oat 环境
# 前置：dex2oatd / dex2oat（从 artroot.raw 提取的 aarch64 本体）已 scp 到本目录。
set -e
cd /root/x86guest

echo "=== ① 解包 merged initrd 出完整系统树（dex2oat 的运行环境与输入 jar）==="
rm -rf art-tree
mkdir -p art-tree
zstd -dc art_initrd_merged.gz | cpio -id --quiet -D art-tree
echo "顶层: $(ls art-tree | tr '\n' ' ')"
ls -la art-tree/system/bin/dex2oat* 2>/dev/null || echo "（initrd 里无 dex2oat——预期，本体外置）"
echo "framework jar 抽查: $(ls -la art-tree/system/framework/core-oj.jar 2>/dev/null)"

echo "=== ② qemu-user 跑 dex2oatd --version（-L 指向树，库走 tree/system/lib64）==="
chmod +x dex2oatd dex2oat 2>/dev/null || true
/usr/bin/qemu-aarch64-static -L /root/x86guest/art-tree ./dex2oatd --version 2>&1 | head -8
echo "EXIT=$?"
