#!/bin/bash
# 带信号哨兵日志跑 Guard 壳验收，抓崩溃前最后的信号序列（108：console 有人读，不会管道堵死）
# 前置：init_x86.sh 的 artlaunch 启动行带 CATCLAW_SIGLOG=1（见下面 sed 幂等注入）
cd /root/x86guest || exit 1
if ! grep -q 'CATCLAW_SIGLOG' init_x86.sh; then
    sed -i 's|^LD_PRELOAD=/proppreload.so /system/bin/artlaunch|export CATCLAW_SIGLOG=1\nLD_PRELOAD=/proppreload.so /system/bin/artlaunch|' init_x86.sh
    echo "init_x86.sh 已注入 CATCLAW_SIGLOG"
fi
grep -n 'CATCLAW_SIGLOG' init_x86.sh
bash mk_x86_initrd.sh >/dev/null 2>&1
bash mi_test.sh 2>&1 | head -2
python3 bench_guard.py 2>&1 | tail -3
echo ===信号序列尾部===
grep -a '\[sig\]' qemu-boot.log | tail -20
echo ===崩溃前后日志===
grep -an -E 'Fatal|signal|SpiderDebug|自定义爬虫|dlopen|崩溃' qemu-boot.log | tail -8
