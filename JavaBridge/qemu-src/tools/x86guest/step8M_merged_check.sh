#!/bin/bash
# §6.10 Step8M：新合并版验证——桥 pong + harness 回连 + image 状态
cd /root/x86guest
echo '=== 进程 ==='
pgrep -af 'qemu-system' | head -2
echo '=== 桥 ping（18602）==='
python3 /root/x86guest/bridge_ping.py 2>&1 | head -3
echo '=== harness 回连迹象（控制端日志）==='
ls -la /root/x86guest/merged-boot.log 2>/dev/null
grep -a 'thunder\|harness\|18080\|task' /root/x86guest/merged-boot.log 2>/dev/null | head -8
echo '=== image/dex2oat 检查 ==='
grep -ac 'Could not create image space' /root/x86guest/merged-boot.log 2>/dev/null
grep -ac 'dex2oat' /root/x86guest/merged-boot.log 2>/dev/null
