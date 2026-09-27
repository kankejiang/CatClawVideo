#!/usr/bin/env python3
"""在 /root/x86guest/init_x86.sh 的 CATCLAW_JVM_EXTRA 上加 -Xcheck:jni（幂等）。
CheckJNI 会在 JNI 调用违规（null 引用、类型不符等）时打日志，而不是让转译执行的
ARM 代码静默解引用 NULL 崩掉（§6.6 定位手段）。"""
import io, os, sys

P = "/root/x86guest/init_x86.sh"
s = io.open(P, encoding="utf-8").read()

if "check:jni" in s:
    print("已有 check:jni，跳过")
    sys.exit(0)

old = 'CATCLAW_JVM_EXTRA="-Xnoimage-dex2oat -Xnodex2oat"'
new = 'CATCLAW_JVM_EXTRA="-Xnoimage-dex2oat -Xnodex2oat -Xcheck:jni"'
if old not in s:
    print("!! 锚点未找到，未改动")
    sys.exit(1)

s = s.replace(old, new, 1)
io.open(P, "w", encoding="utf-8", newline="\n").write(s)
print("已加 -Xcheck:jni")
for line in s.splitlines():
    if "CATCLAW_JVM_EXTRA" in line:
        print(line)
