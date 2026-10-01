#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针 3｜属性依赖审计（只读）。

要回答的问题（T4 §四.4）：**属性服务到底必须可写吗**。
判据不是"感觉要可写"，而是三个可数的事实：
  ① rc/框架在启动过程**写**多少个非 `ro.` 属性（写 = 我们 per-process 编译期表永远满足不了的集合）；
  ② rc/框架**读**哪些属性而我们的表里没有（缺 = 起来后走错分支）；
  ③ `plat_property_contexts` 里有多少前缀带 SELinux context（= 真 init 的写权限模型）。

对照对象是 `JavaBridge/qemu-src/art/props_gen_x64.h`（`gen_props.py` 从 build.prop 生成、
编进 `proppreload.so` 的那张表 —— 就是"我们现在有什么"的权威来源）。

用法：python audit_props.py [--source <initrd|dir>] [--table <props_gen_x64.h>]
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
DEFAULT_TABLE = os.path.normpath(os.path.join(
    HERE, "..", "..", "JavaBridge", "qemu-src", "art", "props_gen_x64.h"))

PROP_RE = re.compile(r"\{\s*\"([^\"]+)\"\s*,\s*\"([^\"]*)\"\s*\}")
WRITE_RE = re.compile(r"(?m)^\s*setprop\s+([\w.\-]+)\s+")
TRIG_RE = re.compile(r"(?m)^\s*on\s+property:([\w.\-]+)=")
SUBST_RE = re.compile(r"\$\{([\w.\-]+)(?::-\S*?)?\}")
CTX_RE = re.compile(r"^([\w.\-]+)\s+(\S+)\s+(prefix|exact|specific_prefix)?", re.M)


def load_table(path):
    """两种格式都吃：我们的编译期 C 表 `{ "k", "v" },` 与真机的 `build.prop`（`k=v`）。
    这样同一支探针能同时跑在 guest 与 108 参照系上 —— 换输入不换判据才叫对照。"""
    if not path or not os.path.exists(path):
        return {}
    t = open(path, encoding="utf-8", errors="replace").read()
    out = {k: v for k, v in PROP_RE.findall(t)}
    if not out:
        for ln in t.splitlines():
            s = ln.strip()
            if not s or s.startswith("#") or "=" not in s:
                continue
            k, v = s.split("=", 1)
            if re.fullmatch(r"[\w.\-]+", k.strip()):
                out[k.strip()] = v.strip()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=DEFAULT_IMG)
    ap.add_argument("--prefix", default="")
    ap.add_argument("--table", default=DEFAULT_TABLE)
    ap.add_argument("--top", type=int, default=25)
    a = ap.parse_args()

    table = load_table(a.table)
    print("== audit_props ==")
    print("对照表: %s" % a.table)
    print("  我们的编译期属性表: %d 条（其中 ro. %d 条 / 非 ro. %d 条）" %
          (len(table), sum(1 for k in table if k.startswith("ro.")),
           sum(1 for k in table if not k.startswith("ro."))))

    want = lambda n: n.endswith(".rc") or n.endswith("property_contexts")
    ents, fp = C.load(a.source, want=want, prefix=a.prefix)
    print("来源: %s（rc 与 property_contexts 已读回内容）" % fp)

    writes = collections.Counter()
    reads = collections.Counter()
    who = collections.defaultdict(set)
    for e in ents:
        if not e.data or not e.name.endswith(".rc"):
            continue
        t = e.text
        for m in WRITE_RE.finditer(t):
            writes[m.group(1)] += 1
            who[m.group(1)].add(os.path.basename(e.name))
        for m in TRIG_RE.finditer(t):
            reads[m.group(1)] += 1
            who[m.group(1)].add(os.path.basename(e.name))
        for m in SUBST_RE.finditer(t):
            reads[m.group(1)] += 1
            who[m.group(1)].add(os.path.basename(e.name))

    writable_req = {k: c for k, c in writes.items() if not k.startswith("ro.")}
    ro_written = {k: c for k, c in writes.items() if k.startswith("ro.")}
    print("\n① rc 在启动过程写的属性: 共 %d 个键（非 ro. %d 个 ⇒ 编译期表**不可能**满足；"
          "ro. 被 setprop %d 个）" % (len(writes), len(writable_req), len(ro_written)))
    C.table(sorted(((k, c, ", ".join(sorted(who[k])[:2]))) for k, c in writable_req.items())[:a.top],
            ["属性（要写）", "次数", "来自"], "非 ro. 写入项（前 %d）" % a.top)

    missing_ro = sorted(k for k in reads if k.startswith("ro.") and k not in table)
    have_read = sorted(k for k in reads if k in table)
    print("\n② rc 读的属性 %d 个键：表里已有 %d 个，缺 %d 个" %
          (len(reads), len(have_read), len(missing_ro)))
    C.table([(k, reads[k], ", ".join(sorted(who[k])[:2])) for k in missing_ro[:a.top]],
            ["属性（要读）", "次数", "来自"], "我们表里没有的只读属性（前 %d）" % a.top)

    # 框架硬前置：这几个键决定 zygote 起不起得来 / boot 完成的广播发不发
    KEY = ["ro.zygote", "ro.boot.hardware", "ro.hardware", "ro.product.cpu.abilist",
           "ro.product.cpu.abilist64", "ro.apex.updatable", "ro.config.low_ram",
           "dalvik.vm.heapsize", "dalvik.vm.isa.arm64.variant", "ro.build.version.sdk",
           "sys.boot_completed", "ro.bootimage.build.fingerprint", "persist.sys.dalvik.vm.lib.2",
           "ro.dalvik.vm.native.bridge", "ro.control_privapp_permissions", "ro.kernel.qemu"]
    C.table([(k, "有" if k in table else "缺", table.get(k, ""), reads.get(k, 0), writes.get(k, 0))
             for k in KEY], ["关键属性", "我们的表", "值", "被读", "被写"],
            "框架启动关键属性对照（值截断到 24 字符）")

    # ③ property_contexts：真 init 的写权限模型有多大
    pc = [e for e in ents if e.data and e.name.endswith("property_contexts")]
    print("\n③ property_contexts 文件 %d 个" % len(pc))
    pref = collections.Counter()
    total = 0
    for e in pc:
        for m in CTX_RE.finditer(e.text):
            k = m.group(1)
            if k.startswith("#") or not k:
                continue
            total += 1
            root = k.split(".")[0]
            pref[root] += 1
    print("   条目 %d；按前缀: %s" % (total, dict(pref.most_common(12))))
    nonro_ctx = sum(c for k, c in pref.items() if k != "ro")
    print("   ⇒ 需要写权限模型的前缀（非 ro.*）占 %d/%d" % (nonro_ctx, total))

    print("\n结论（判读规则见 docs/research/framework/探针清单与判读.md）:")
    print("  · 非 ro. 写入 %d 个键 ⇒ 属性服务**必须可写**，shim 路线要造的是「一个进程写、全机可见」的共享区，"
          % len(writable_req))
    print("    而不是再加几条编译期常量；")
    print("  · 缺读的 ro. 属性 %d 个 ⇒ 这部分是**便宜的**（补进 build.prop/gen_props 即可）；" % len(missing_ro))
    print("  · 两者相加就是「继续 shim」的真实工作量。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
