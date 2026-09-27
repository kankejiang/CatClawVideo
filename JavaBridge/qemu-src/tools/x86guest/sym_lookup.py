#!/usr/bin/env python3
# 解析 libart 里 native_bridge_art_callbacks_ 各函数指针的符号名
import re
addrs = ["71cdc0", "71d540", "71dc30", "d077d", "b867a", "71f0e0", "9e64b", "9b23c", "71f9d0"]
syms = {}
with open("/tmp/artsyms.txt", errors="replace") as f:
    for line in f:
        m = re.match(r"\s*\d+:\s+([0-9a-f]{16})\s+\d+\s+\w+\s+\w+\s+\w+\s+\S+\s+(.+)", line)
        if m:
            syms.setdefault(int(m.group(1), 16), m.group(2).strip())
for a in addrs:
    v = int(a, 16)
    print("0x%-8s -> %s" % (a, syms.get(v, syms.get(v + 1, "(未导出/未找到)"))))
