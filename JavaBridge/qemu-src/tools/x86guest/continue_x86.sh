#!/bin/bash
# x86 mini guest 续跑脚本：防重追加 namespace → 组装 → KVM 测试 → 诊断
# 用法（Windows 侧）：scp 后 ssh root@10.0.0.108 "bash /root/x86guest/continue_x86.sh"
cd /root/x86guest || exit 1
if ! grep -q "namespace.default.links = com_android_art" ld_config_x86.txt; then
cat >> ld_config_x86.txt <<'APXNS'
namespace.default.links = com_android_art
namespace.default.link.com_android_art.allow_all_shared_libs = true
namespace.com_android_art.isolated = false
namespace.com_android_art.visible = true
namespace.com_android_art.search.paths = /apex/com.android.art/lib64
namespace.com_android_art.permitted.paths = /apex/com.android.art/lib64:/system/lib64:/data:/data/catclaw
APXNS
echo "[continue] namespace 定义已追加"
else
echo "[continue] namespace 定义已存在，跳过追加"
fi
echo "[continue] 组装 initrd..."
bash mk_x86_initrd.sh 2>&1 | tail -2
echo "[continue] KVM 启动测试..."
bash run_x86_test.sh > /dev/null 2>&1
bash diag.sh 2>&1 | tail -26