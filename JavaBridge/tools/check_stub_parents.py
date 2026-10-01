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

⚠ 2026-10-02 判 22 处"不一致"时发现**闸门自己造了假阳性**（三类，全部已修 + 自检覆盖）：
  ① import 解析把**包名**当成了类全名（`import android.view.ViewGroup;` → 存成 `android.view`），
     于是 LinearLayout/FrameLayout/ImageView/TextView/ProgressBar/Activity/WebView 全被判错；
  ② javap 对**泛型类**的输出是 `class android.widget.ArrayAdapter<T> extends …`，
     旧正则名字后直接要 `extends` ⇒ 撞上 `<T>` 就退化成 Object（AdapterView/ArrayAdapter 假阳性）；
  ③ `extends RuntimeException` 这类 **java.lang 隐式导入**没被解析成全名 ⇒ 永远判错
     （ActivityNotFoundException 假阳性）。
⇒ 闸门报错不等于桩真错：**每条都要能指出"谁把该类当父类要求的那个类型用"**（见 --explain 输出）。

用法：
    python check_stub_parents.py            # 有不一致就 exit 1 并列出来
    python check_stub_parents.py --explain  # 附每条的判读依据（真机全名/桩里写的全名/来源）
    python check_stub_parents.py --selftest # 证明检测真的会拦住「父类写错/漏写」，且三类假阳性归零
"""
import io, os, re, subprocess, sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
BRIDGE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
SRC = os.path.join(BRIDGE, "src", "android")
SDK = os.path.expandvars(r"%LOCALAPPDATA%\Android\Sdk\platforms")
DECL = re.compile(r"(?:public\s+|final\s+|abstract\s+|static\s+)*class\s+(\w+)")
# ⚠ 必须锚定**文件名那个类**的声明：旧写法 `EXT.search(text)` 取的是"文件里第一个 extends"，
#   于是 `content/pm/PackageManager.java`（顶层 `class PackageManager {` 无父类，
#   但文件里有嵌套的 `class NameNotFoundException extends Exception`）被判成
#   "桩父类=Exception" —— 假阳性第 ④ 类（Resources 同形）。
EXT_TMPL = r"(?:public\s+|final\s+|abstract\s+|static\s+)*class\s+%s(?:<[^>]*>)?" \
           r"[^{]*?\bextends\s+([\w.]+)"
# javap 名字与 extends 之间可能有泛型参数与 implements：`class a.b.C<T> extends a.b.D implements …`
JAVAP_LINE = re.compile(r"^(?:public\s+)?(?:final\s+|abstract\s+)?class\s+([\w.$]+)(?:<[^>]*>)?"
                        r"(?:\s+extends\s+([\w.$]+)(?:<[^>]*>)?)?", re.M)
IMPORT = re.compile(r"^\s*import\s+(?:static\s+)?([\w.]+?)\.(\w+);", re.M)
JAVAP = os.environ.get("JAVAP", "javap")


def android_jar():
    for lvl in ("android-33",):
        p = os.path.join(SDK, lvl, "android.jar")
        if os.path.exists(p):
            return p
    hits = sorted(d for d in os.listdir(SDK) if os.path.exists(os.path.join(SDK, d, "android.jar")))
    return os.path.join(SDK, hits[-1], "android.jar")


def resolve(simple, pkg, imports):
    """简单名 → **候选全名集合**（本包 / import / java.lang 隐式导入 / 原样）。

    返回集合而不是单值，是为了让比对能接受"同名同类的不同写法"，同时**不放过**真错：
    真机父类必须落在候选集里才算一致。旧实现只返回一个值，java.lang.* 隐式导入永远解不出，
    于是 `extends RuntimeException` 被当成与 `java.lang.RuntimeException` 不一致（假阳性）。
    """
    if "." in simple:
        return {simple}
    cand = {pkg + "." + simple, imports.get(simple), "java.lang." + simple, simple}
    return {c for c in cand if c}


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
            for m in IMPORT.finditer(text):
                # ⚠ 全名 = 组1 + "." + 组2。旧实现把组1（**包名**）当成了类全名，
                #   于是 `import android.view.ViewGroup;` 解析成 `android.view` ⇒ 一片假阳性。
                imports[m.group(2)] = m.group(1) + "." + m.group(2)
            cls = f[:-5]
            em = re.search(EXT_TMPL % re.escape(cls), text)
            raw = em.group(1) if em else "java.lang.Object"
            out.append((pkgname + "." + cls, raw, resolve(raw, pkgname, imports)))
    return sorted(out)


def real_parents(names, jar):
    try:
        p = subprocess.run([JAVAP, "-classpath", jar] + names, capture_output=True)
        text = p.stdout.decode("utf-8", "replace")
    except Exception as e:
        print("!! javap 跑不起来（%s）—— 用 JAVAP=<路径> 指定" % e)
        return None
    parents = {}
    for m in JAVAP_LINE.finditer(text):
        parents[m.group(1)] = m.group(2) or "java.lang.Object"
    return parents


def diff(pairs, parents, explain=False):
    """pairs = [(类全名, 桩里写的父类原文, 候选全名集合)]。真机父类不在候选集里才算不一致。"""
    bad = []
    for item in pairs:
        name, raw, cand = item
        want = parents.get(name)
        if want is None:
            continue                        # android.jar 里没有：自造类型，不判
        if want in cand:
            continue
        bad.append((name, raw, want, "本包/import/java.lang 候选=%s" % sorted(cand))
                   if explain else (name, raw, want))
    return bad


def main():
    if "--selftest" in sys.argv:
        # 正控制：真错必须被抓到（不修就必须报）
        fake = {"android.widget.SeekBar": "android.widget.ProgressBar",
                "android.widget.LinearLayout": "android.view.ViewGroup",
                "android.content.ActivityNotFoundException": "java.lang.RuntimeException",
                "android.widget.ArrayAdapter": "android.widget.BaseAdapter"}
        bad = diff([("android.widget.SeekBar", "View", {"android.widget.View", "View"}),
                    ("android.widget.SeekBar", "java.lang.Object", {"java.lang.Object"}),
                    ("android.widget.LinearLayout", "android.view", {"android.view"}),
                    ("android.widget.ArrayAdapter", "Object", {"android.widget.Object"})], fake)
        # 三类假阳性归零：import 全名 / java.lang 隐式导入 / javap 泛型类
        okcases = diff([("android.widget.LinearLayout", "ViewGroup",
                         {"android.widget.ViewGroup", "android.view.ViewGroup",
                          "java.lang.ViewGroup", "ViewGroup"}),
                        ("android.content.ActivityNotFoundException", "RuntimeException",
                         {"android.content.RuntimeException", "java.lang.RuntimeException",
                          "RuntimeException"}),
                        ("android.widget.ArrayAdapter", "BaseAdapter",
                         {"android.widget.BaseAdapter", "java.lang.BaseAdapter", "BaseAdapter"})],
                       fake)
        print("自检: 真错样本命中=%d（要 4），三类假阳性命中=%d（要 0）" % (len(bad), len(okcases)))
        for n, got, want in okcases:
            print("   仍误报: %-42s 桩=%-18s 真机=%s" % (n, got, want))
        ok = len(bad) == 4 and not okcases
        print("自检 %s" % ("PASS" if ok else "FAIL —— 闸门本身失效"))
        return 0 if ok else 1

    explain = "--explain" in sys.argv
    jar = android_jar()
    pairs = stub_classes()
    print("android.jar: %s；桩类 %d 个" % (jar, len(pairs)))
    parents = real_parents([p[0] for p in pairs], jar)
    if parents is None:
        return 2
    bad = diff(pairs, parents, explain)
    for row in bad:
        name, got, want = row[:3]
        print("  %-46s 桩父类=%-30s 真机=%s" % (name, got, want))
        if explain:
            print("  %48s %s" % ("", row[3]))
    if bad:
        print("!! 继承树不一致 %d 处 —— ART 校验期会拒绝用到它们的壳类（VerifyError）" % len(bad))
        print("   判读：每条都要能说出「哪个壳类把它当父类要求的那个类型用」才算真错；"
              "说不出来的先当待判，别改桩。")
        return 1
    print("桩继承树全部与 android.jar 一致")
    return 0


sys.exit(main())
