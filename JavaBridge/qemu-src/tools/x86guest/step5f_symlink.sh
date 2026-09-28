#!/bin/bash
# §6.10 Step5f：framework 根下建 boot* 相对符号链接（Android 标准布局）
# 根因：ART 按「BCP 首 jar 目录 + boot.art」推导 image 路径 = /system/framework/boot.art，
#       而自产镜像在 arm64/ 子目录 → 永远 fallback imageless。
cd /root/x86guest/art-tree/system/framework || exit 1
for f in arm64/boot*.art arm64/boot*.oat arm64/boot*.vdex; do
  [ -e "$f" ] || continue
  ln -sf "$f" "$(basename "$f")"
done
echo "=== 根下链接（前 12）==="
ls -la | grep '^l' | head -12
echo "总链接数: $(ls -la | grep -c '^l')"
echo "=== 校验解析 ==="
ls -lL boot.art 2>/dev/null | head -1
