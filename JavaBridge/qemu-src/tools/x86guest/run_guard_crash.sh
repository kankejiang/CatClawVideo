#!/bin/bash
# Guard 壳崩溃现场抓取：组装 → 起 VM → bench_guard → 输出信号/PC/崩溃点/退出码
cd /root/x86guest || exit 1
bash mk_x86_initrd.sh >/dev/null 2>&1
bash mi_test.sh 2>&1 | head -2
python3 bench_guard.py 2>&1 | tail -2
echo ===崩溃现场（信号/PC/模块）===
grep -a '\[sig\]' qemu-boot.log | tail -6
echo ===上下文===
grep -a -E 'SpiderDebug|自定义爬虫|退出码|DexClassLoader' qemu-boot.log | tail -5
