#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针 5｜APK 到底需要 framework 的哪些部分（只读，零外部依赖）。

为什么必须有这支：路线 (C)「不做完整框架、只靠我们的 ART 启动器 + 有限 UI 桩」能不能成立，
唯一诚实的判据是**APK 实际引用的 framework 面有多大**，而不是"感觉 TVBox 用得不多"。
所以这里直接把 APK 的 dex 类型表解出来，和我们的 android 桩命名空间求差集。

自己解 DEX 而不调 dexdump：省掉 SDK 路径依赖，Windows/108 同一支脚本能跑。

用法：
  python audit_apk_needs.py [--apk <TVBox.apk>] [--stubs <JavaBridge/src>]
"""
import argparse
import collections
import os
import re
import sys
import zipfile

# 同 cpioimg.py：GBK 控制台下 ✔/⇒ 会崩，且重定向时证据被截断 ⇒ 强制 UTF-8 输出。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_APK = r"D:\Code\sourceCode-ccc25f6\app\build\outputs\apk\java64\debug\TVBox_debug-java64.apk"
DEFAULT_STUBS = os.path.normpath(os.path.join(HERE, "..", "..", "JavaBridge", "src"))


def uleb(data, off):
    r = shift = 0
    while True:
        b = data[off]
        off += 1
        r |= (b & 0x7F) << shift
        if not b & 0x80:
            return r, off
        shift += 7


def dex_types(blob):
    """返回这个 dex 里所有类型描述符（type_ids → string_ids）。"""
    string_ids_size = int.from_bytes(blob[56:60], "little")
    string_ids_off = int.from_bytes(blob[60:64], "little")
    type_ids_size = int.from_bytes(blob[64:68], "little")
    type_ids_off = int.from_bytes(blob[68:72], "little")
    strs = []
    for i in range(string_ids_size):
        so = int.from_bytes(blob[string_ids_off + i * 4:string_ids_off + i * 4 + 4], "little")
        n, p = uleb(blob, so)
        strs.append(blob[p:p + n].decode("utf-8", "replace"))
    out = []
    for i in range(type_ids_size):
        idx = int.from_bytes(blob[type_ids_off + i * 4:type_ids_off + i * 4 + 4], "little")
        if idx < len(strs):
            out.append(strs[idx])
    return out


def stub_classes(src_dir):
    """我们手写桩提供的 android.* 类（按源文件树推类名，含内部类）。"""
    out = set()
    for dp, dns, fns in os.walk(src_dir):
        for fn in fns:
            if not fn.endswith(".java"):
                continue
            rel = os.path.relpath(os.path.join(dp, fn), src_dir).replace("\\", "/")
            cls = rel[:-5].replace("/", ".")
            if cls.startswith("android.") or cls.startswith("dalvik.") or cls.startswith("org.json"):
                out.add(cls)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apk", default=DEFAULT_APK)
    ap.add_argument("--stubs", default=DEFAULT_STUBS)
    ap.add_argument("--top", type=int, default=18)
    a = ap.parse_args()

    print("== audit_apk_needs ==")
    if not os.path.exists(a.apk):
        print("APK 不存在: %s ⇒ 无法给出 (C) 的数字判据" % a.apk)
        return 2
    types = set()
    dexes = []
    with zipfile.ZipFile(a.apk) as z:
        dexes = [n for n in z.namelist() if re.fullmatch(r"classes\d*\.dex", n)]
        for n in dexes:
            types |= set(dex_types(z.read(n)))
    print("APK: %s" % a.apk)
    print("  dex 文件 %d 个；类型描述符 %d 个" % (len(dexes), len(types)))

    def desc2cls(d):
        d = d.lstrip("[")
        if d.startswith("L") and d.endswith(";"):
            return d[1:-1].replace("/", ".")
        return None

    cls = {desc2cls(t) for t in types if t.startswith("[") or t.startswith("L")}
    cls = {c for c in cls if c}
    android = sorted(c for c in cls if c.startswith("android.") or c.startswith("dalvik.")
                     or c.startswith("org.json") or c.startswith("org.apache"))
    framework = sorted(c for c in cls if c.startswith(("android.app.", "android.content.",
                                                       "android.view.", "android.widget.",
                                                       "android.media.", "android.os.",
                                                       "android.net.", "android.telephony.",
                                                       "android.provider.", "android.graphics.",
                                                       "android.webkit.", "android.database.",
                                                       "android.text.")))
    stubs = stub_classes(a.stubs)
    have = sorted(c for c in framework if c in stubs)
    need = sorted(c for c in framework if c not in stubs)

    buckets = collections.Counter(".".join(c.split(".")[:3]) for c in framework)
    C_rows = [(k, v, "有桩" if any(x.startswith(k) for x in have) else "无桩")
              for k, v in buckets.most_common(a.top)]
    print("  android.*/dalvik.* 引用类: %d；其中 framework 包: %d" % (len(android), len(framework)))
    print("\n── APK 引用的 framework 面包（按前三段聚合）──")
    for r in C_rows:
        print("   %-34s %4d 个类   %s" % r)

    print("\n── 与我们的 android 桩求差集（这就是 (C) 路线的真实缺口）──")
    print("   桩里有 %d 个被引用类；缺 %d 个" % (len(have), len(need)))
    for k, _ in buckets.most_common(a.top):
        miss = [c for c in need if c.startswith(k)]
        if miss:
            print("   %-30s 缺 %3d 个，例: %s" % (k, len(miss), ", ".join(miss[:3])))

    # 生命周期类：这几个不是"再写几个方法"能糊过去的 —— 它们要求 framework 真的在管进程
    HARD = ["android.app.Activity", "android.app.Service", "android.app.Application",
            "android.content.ContentProvider", "android.content.BroadcastReceiver",
            "android.app.AlarmManager", "android.app.NotificationManager",
            "android.view.WindowManager", "android.view.Choreographer",
            "android.os.Looper", "android.os.Handler", "android.os.HandlerThread",
            "android.webkit.WebView", "android.media.MediaPlayer",
            "android.media.session.MediaSession", "android.app.ActivityManager"]
    print("\n── 硬骨头（引用到就说明需要真 framework 的进程/生命周期管理）──")
    for h in HARD:
        used = h in cls or any(c == h for c in cls)
        print("   %-42s %s   桩:%s" % (h, "引用" if used else "未引用",
                                       "有" if h in stubs else "无"))
    print("\n结论（判读规则见 docs/research/framework/探针清单与判读.md）:")
    print("  · 缺 %d 个 framework 类 ⇒ 路线 (C) 的「继续手写桩」成本是按包计的工作量，" % len(need))
    print("    不是「再补几个方法」；")
    print("  · 硬骨头里只要有一个是「引用了且需要真生命周期」，(C) 就只能覆盖不需要它的功能子集。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
