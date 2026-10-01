#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""桩纪律闸门：ViewGroup 子系的桩**不许有空实现的 addView/onMeasure**。

为什么需要（2026-10-01 扫码链路实测）：桩生成器会给「jar 引用到但桩缺失」的成员按 android-33
签名补**空方法**，于是 `ScrollView.addView(View)` 这类关键容器方法被空覆写抢在父类真实实现
之前 —— 子节点静默丢掉，视图树只剩一个空壳，宿主收到没有二维码的对话框。同一形状在
LinearLayout 上也踩过一次（见其 addView 的注释）。

用法：
    python check_stubs.py            # 扫 JavaBridge/src/android，有问题就 exit 1
    python check_stubs.py --selftest # 自检：确认这段检测真的会拦住退化（不修就必须坏）
"""
import io, os, re, sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "android"))
BAD = re.compile(r"public\s+void\s+(addView|onMeasure|removeView|removeAllViews|addContentView|dispatchDraw|drawChild)\s*\([^)]*\)\s*\{\s*\}")
CONTAINER = re.compile(r"class\s+\w+\s+extends\s+(ViewGroup|FrameLayout|LinearLayout|ScrollView|HorizontalScrollView|ListView|RelativeLayout|GridLayout|ViewFlipper|AdapterView)")

def scan_text(name, text):
    """返回该源文件里「容器类 + 空的数据方法覆写」的命中行。"""
    if not CONTAINER.search(text):
        return []
    out = []
    for i, line in enumerate(text.splitlines(), 1):
        m = BAD.search(line)
        if m:
            out.append((name, i, m.group(1), line.strip()))
    return out

def main():
    if "--selftest" in sys.argv:
        bad = scan_text("ScrollView.java", "class ScrollView extends FrameLayout {"
                        " public void addView(android.view.View p0) { }\n}")
        good = scan_text("ScrollView.java", "class ScrollView extends FrameLayout {"
                         " public void addView(android.view.View p0) { super.addView(p0); }\n}")
        ok = len(bad) == 1 and not good
        print("自检: 空覆写样本命中=%d（要 1），正常样本命中=%d（要 0）" % (len(bad), len(good)))
        print("自检 %s" % ("PASS" if ok else "FAIL —— 闸门本身失效"))
        return 0 if ok else 1

    hits = []
    n = 0
    for dirpath, _, files in os.walk(ROOT):
        for f in files:
            if not f.endswith(".java"):
                continue
            p = os.path.join(dirpath, f)
            text = open(p, encoding="utf-8", errors="replace").read()
            n += 1
            hits += scan_text(os.path.relpath(p, ROOT), text)
    print("扫了 %d 个桩源文件" % n)
    for name, line, meth, raw in hits:
        print("  %s:%d 空覆写 %s —— 子节点/尺寸会被静默丢掉: %s" % (name, line, meth, raw))
    if hits:
        print("!! 未通过 %d 处：容器桩的数据方法必须转交 super（见本文件顶部说明）" % len(hits))
        return 1
    print("桩纪律检查全部通过")
    return 0

sys.exit(main())
