#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针 2｜init rc 审计（只读）。

要回答的问题（T4 §四.3）：**真 init 路线要落地多少配置**。rc 是 init 唯一的输入，
所以"有多少 service / 多少 on 触发器 / 引用的可执行文件在不在"就是那条路线的工作量量级。

顺带把「我们现在的自编排 init 跳过了哪些副作用」量化（mkdir/chmod/write /proc/sys 的条数），
这些正是"起不了 APK"最可能的隐性原因（缺目录、缺权限、缺内核参数）。

用法：python audit_rc.py [--source <initrd|dir>] [--top 20]
"""
import argparse
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cpioimg as C

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_IMG = os.path.normpath(os.path.join(
    HERE, "..", "..", "CatClawVideo.Maui", "QemuGuest", "x86guest", "art_initrd_x64.gz"))

# 框架启动链上的关键服务（少一个就断一环）：init→ueventd/servicemanager→apexd→zygote→system_server
CRITICAL = ["ueventd", "servicemanager", "hwservicemanager", "apexd", "apexd-bootstrap", "vold",
            "zygote", "zygote_secondary", "logd", "tombstoned", "statsd", "lmkd", "bpfloader",
            "surfaceflinger", "android.hardware.graphics.composer", "gatekeeperd", "credstore",
            "keystore", "gsid", "media"]


def parse_rc(text):
    """粗解析 rc：service / on / 指令计数。rc 语法是行式的，不需要完整解析器。"""
    out = {"services": [], "on": [], "setprop": [], "mkdir": 0, "chmod": 0, "chown": 0,
           "write": [], "exec": [], "trigger": [], "class": collections.Counter(),
           "flags": collections.Counter(), "lines": 0}
    cur = None
    for ln in text.splitlines():
        s = ln.strip()
        out["lines"] += 1
        if not s or s.startswith("#"):
            continue
        toks = s.split()
        k = toks[0]
        if k == "service" and len(toks) >= 3:
            name, binary = toks[1], toks[2]
            out["services"].append((name, binary, " ".join(toks[3:])))
            cur = name
        elif k == "on" and len(toks) >= 2:
            out["on"].append(" ".join(toks[1:]))
            cur = None
        elif k == "setprop" and len(toks) >= 3:
            out["setprop"].append(" ".join(toks[1:]))
        elif k == "mkdir":
            out["mkdir"] += 1
        elif k in ("chmod", "chown", "chroot", "rm"):
            out["chmod" if k == "chmod" else "chown"] += 1
        elif k == "write" and len(toks) >= 2:
            out["write"].append(toks[1])
        elif k in ("exec", "exec_start", "exec_background"):
            out["exec"].append(" ".join(toks[1:]))
        elif k == "trigger":
            out["trigger"].append(s)
        elif k == "class" and cur and len(toks) >= 2:
            out["class"][toks[1]] += 1
        if cur and k in ("disabled", "oneshot", "critical", "restart_period", "setenv", "user",
                         "group", "seclabel", "capabilities", "socket"):
            out["flags"][k] += 1
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=DEFAULT_IMG)
    ap.add_argument("--prefix", default="")
    ap.add_argument("--top", type=int, default=20)
    a = ap.parse_args()

    want = lambda n: n.endswith(".rc")
    ents, fp = C.load(a.source, want=want, prefix=a.prefix)
    rcs = [e for e in C.rc_files(ents) if e.data]
    # ⚠ 符号链接也算「可执行在位」：`/system/bin/app_process` 在 cpio 里常是 symlink（指向 app_process64）
    regs = {e.name for e in ents if C.is_reg(e.mode) or C.is_link(e.mode)}
    print("== audit_rc ==")
    print("来源: %s" % fp)
    print("rc 文件: %d 个，共 %d 行" % (len(rcs), sum(len(e.text.splitlines()) for e in rcs)))

    tot = {"services": [], "on": [], "setprop": [], "mkdir": 0, "chmod": 0, "chown": 0,
           "write": [], "exec": [], "trigger": []}
    classes = collections.Counter()
    flags = collections.Counter()
    per_file = []
    for e in rcs:
        p = parse_rc(e.text)
        for k in ("services", "on", "setprop", "write", "exec", "trigger"):
            tot[k] += [(e.name, *v) if k == "services" else (e.name, v) for v in p[k]]
        tot["mkdir"] += p["mkdir"]
        tot["chmod"] += p["chmod"] + p["chown"]
        classes.update(p["class"])
        flags.update(p["flags"])
        per_file.append((e.name, len(p["services"]), len(p["on"]), e.size))

    print("\n总量级（真 init 路线要吃下的配置）:")
    print("  service 定义     %d 条" % len(tot["services"]))
    print("  on 触发器        %d 条" % len(tot["on"]))
    print("  setprop          %d 条" % len(tot["setprop"]))
    print("  mkdir/chmod/chown %d 条" % (tot["mkdir"] + tot["chmod"]))
    print("  write /sys|/proc %d 条" % len(tot["write"]))
    print("  exec/exec_start  %d 条" % len(tot["exec"]))
    print("  class 分布: %s" % dict(classes.most_common(8)))
    print("  服务属性: %s" % dict(flags.most_common(8)))

    trig = collections.Counter(v for _, v in tot["on"])
    C.table([(k, c) for k, c in trig.most_common(a.top)], ["on 触发器", "次数"],
            "触发器分布（框架启动链按这个顺序走）")

    # service → 可执行文件是否真在镜像里（缺一个 ⇒ 真 init 起来后该服务永远 restart 循环）
    rows, missing = [], []
    for fname, name, binary, args in tot["services"]:
        path = binary.lstrip("/")
        ok = path in regs
        rows.append((name, binary, "在" if ok else "缺"))
        if not ok:
            missing.append((name, binary, fname))
    C.table(sorted(missing) or [("(全部在位)", "-", "-")],
            ["service", "可执行", "出处 rc"], "缺失的可执行（真 init 起来后这些服务会卡在 restart 循环）")
    C.table(sorted(rows)[:a.top * 2], ["service", "可执行", "镜像里"],
            "service 引用的可执行文件核对（前 %d）" % (a.top * 2))
    print("  ⇒ service 总数 %d，可执行缺失 %d" % (len(rows), len(missing)))

    crit = [(n, b, "在" if b.lstrip("/") in regs else "缺")
            for _, n, b, _ in tot["services"] if any(c in n for c in CRITICAL)]
    C.table(sorted(crit), ["service", "可执行", "镜像里"], "框架启动链关键服务")

    biggest = sorted(per_file, key=lambda r: -r[1])[:a.top]
    C.table(biggest, ["rc 文件", "service", "on", "字节"], "service 最多的 rc 文件")
    print("\n结论要点（判读规则见 docs/research/framework/探针清单与判读.md）:")
    print("  · 若关键服务可执行齐全 ⇒ 真 init 路线的阻塞点在**属性服务/SELinux/挂载**，不在文件缺失")
    print("  · 若缺 ⇒ 先补文件（工作量以个位数计）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
