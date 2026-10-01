#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""桩继承树闸门：JavaBridge/src/android 里每个桩的父类必须和 android.jar(API 33) 一致。

为什么（2026-10-01 扫码链路实测）：桩的 `android.widget.SeekBar` 声明成了 `extends View`，
而真 Android 是 `SeekBar extends ProgressBar`。壳的类 `merge.A.b0.i()` 把 SeekBar 当 ProgressBar 用
⇒ ART 在**校验期**就拒了这个类：
    VerifyError: 'this' argument 'Precise Reference: android.widget.SeekBar'
                 not instance of 'Reference: android.widget.ProgressBar'
壳那个异步任务整个死掉 ⇒ 二维码对话框根本没建起来（宿主收不到任何带 qr 的 ui-dialog）。
「父类写弱一层」和空覆写一样是静默丢功能，只有跑在真 ART 上才暴露，所以做成常开闸门。

比对规则：源码里写的父类常是简单名（`extends Dialog`），javap 给的是全名（android.app.Dialog），
所以要按**本包 → import → java.lang** 解析简单名再比全名；只声明 `class X {`（隐含 Object）
而真机有父类的也算不一致。

用法：
    python check_stub_parents.py            # 有不一致就 exit 1 并列出来
    python check_stub_parents.py --selftest # 证明检测真的会拦住「父类写错/漏写」
"""
import io, os, re, subprocess, sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
BRIDGE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
SRC = os.path.join(BRIDGE, "src", "android")
SDK = os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk\platforms")
DECL = re.compile(r"(?:public\s+|final\s+|abstract\s+|static\s+)*class\s+(\w+)")
EXT = re.compile(r"(?:public\s+|final\s+|abstract\s+|static\s+)*class\s+\w+[^{]*?\bextends\s+([\w.]+)")
JAVAP = os.environ.get("JAVAP", "javap")


def android_jar():
    for lvl in ("android-33",):
        p = os.path.join(SDK, lvl, "android.jar")
        if os.path.exists(p):
            return p
    hits = sorted(d for d in os.listdir(SDK) if os.path.exists(os.path.join(SDK, d, "android.jar")))
    return os.path.join(SDK, hits[-1], "android.jar")


def resolve(simple, pkg, imports, body):
    """把源码里的父类简单名解析成全名；解不出就返回简单名本身（多半是自造类型）。"""
    if "." in simple:
        return simple
    if simple in imports:
        return imports[simple]
    cand = pkg + "." + simple
    if os.path.exists(os.path.join(SRC, *cand.split(".")[1:]) + ".java"):
        return cand
    return simple          # java.lang.* 或未知名：交给 diff 里的宽松比对


def stub_classes():
    out = []
    for dirpath, _, files in os.walk(SRC):
        for f in files:
            if not f.endswith(".java"):
                continue
            path = os.path.join(dirpath, f)
            text = open(path, encoding="utf-8", errors="replace").read()
            pkg = re.search(r"^\s*package\s+([\w.]+);", text, re.M)
            if not pkg or not DECL.search(text):
                continue
            pkgname = pkg.group(1)
            imports = {}
            for m in re.finditer(r"^\s*import\s+(?:static\s+)?([\w.]+)\.(\w+);", text, re.M):
                imports[m.group(2)] = m.group(1)
            em = EXT.search(text)
            parent = resolve(em.group(1), pkgname, imports, text) if em else "java.lang.Object"
            out.append((pkgname + "." + f[:-5], parent))
    return sorted(out)


def real_parents(names, jar):
    try:
        p = subprocess.run([JAVAP, "-classpath", jar] + names, capture_output=True)
        text = p.stdout.decode("utf-8", "replace")
    except Exception as e:
        print("!! javap 跑不起来（%s）—— 用 JAVAP=<路径> 指定" % e)
        return None
    parents = {}
    for m in re.finditer(r"^(?:public\s+)?(?:final\s+|abstract\s+)?class\s+([\w.$]+)(?:\s+extends\s+([\w.$]+))?",
                         text, re.M):
        parents[m.group(1)] = m.group(2) or "java.lang.Object"
    return parents


def diff(pairs, parents):
    bad = []
    for name, got in pairs:
        want = parents.get(name)
        if want is None:
            continue                        # android.jar 里没有：自造类型，不判
        if got == want or got.split(".")[-1] == want.split(".")[-1] and got.count(".") == 0 and False:
            continue
        if got == want:
            continue
        # 简单名恰好同名同包（`extends Dialog` == android.app.Dialog）在上面已按全名解析；
        # 剩下能对上「同名但不同包」的多半是 import 缺失，按不一致报，交人判
        bad.append((name, got, want))
    return bad


def main():
    if "--selftest" in sys.argv:
        fake = {"android.widget.SeekBar": "android.widget.ProgressBar"}
        bad = diff([("android.widget.SeekBar", "android.view.View"),
                    ("android.widget.SeekBar", "java.lang.Object")], fake)
        good = diff([("android.widget.SeekBar", "android.widget.ProgressBar")], fake)
        print("自检: 错/漏父类样本命中=%d（要 2），对父类样本命中=%d（要 0）" % (len(bad), len(good)))
        ok = len(bad) == 2 and not good
        print("自检 %s" % ("PASS" if ok else "FAIL —— 闸门本身失效"))
        return 0 if ok else 1

    jar = android_jar()
    pairs = stub_classes()
    print("android.jar: %s；桩类 %d 个" % (jar, len(pairs)))
    parents = real_parents([n for n, _ in pairs], jar)
    if parents is None:
        return 2
    bad = diff(pairs, parents)
    for name, got, want in bad:
        print("  %-46s 桩父类=%-28s 真机=%s" % (name, got, want))
    if bad:
        print("!! 继承树不一致 %d 处 —— ART 校验期会拒绝用到它们的壳类（VerifyError）" % len(bad))
        return 1
    print("桩继承树全部与 android.jar 一致")
    return 0


sys.exit(main())
