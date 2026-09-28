#!/bin/bash
# §6.10 Step5b：分层诊断 mmap 1.5GB（内核层 vs qemu 层）
echo '=== host 原生 python mmap 1.5GB ==='
python3 - <<'PYEOF'
import mmap
try:
    m = mmap.mmap(-1, 1610612736)
    print('host mmap 1.5GB OK')
    m.close()
except Exception as e:
    print('host mmap FAIL:', e)
PYEOF
echo '=== ulimit ==='
ulimit -v
echo '=== qemu 层：guest 里 mmap 1.5GB（用 art-tree 里的静态 busybox？busybox 无 mmap applet——用 dd 试探）==='
# 用 qemu 跑一个 aarch64 静态二进制做 mmap 大块测试：直接用 dex2oatd 之外的 busybox（ifconfig 等不适用）
# 简化：用 python 无 arm64 版本——跳过，直接给结论注释
echo '（qemu 层验证略——直接看 ART 内 mem_map 失败时的 kernel 日志）'
dmesg 2>/dev/null | tail -5 || echo '（dmesg 需 root）'
