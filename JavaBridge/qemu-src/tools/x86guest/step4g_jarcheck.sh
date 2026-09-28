#!/bin/bash
# §6.10 Step4g：检查注入后的 jar 里 dex 条目状态（怀疑 stub 原条目与真 dex 同名共存）
cd /root/x86guest/art-tree/system/framework
for j in core-oj.jar core-libart.jar framework.jar ext.jar; do
  echo "=== $j ==="
  unzip -l $j | grep -iE 'class|dex' | head -6
  echo "  条目总数: $(unzip -l $j | tail -1)"
done
