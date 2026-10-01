#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针 1｜APEX 审计（只读）。

要回答的问题（T4 §二.1 / §四.1）：framework 启动到底需要哪些 APEX，我们的 guest 镜像里
**实际**有什么形态 —— 是缺文件、还是有 `.apex` 但没挂载、还是已经以解包目录躺在 `/apex/**` 里。
这三种情况的工期完全不同（前者要造内容，后者只要让 init 认它）。

用法：
  python audit_apex.py                                   # 审计部署件 initrd
  python audit_apex.py --source /mnt/sysimg --prefix ""  # 审计 108 上挂载的 system.img
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cpioimg as C

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_IMG = os.path.normpath(os.path.join(
    HERE, "..", "..", "CatClawVideo.Maui", "QemuGuest", "x86guest", "art_initrd_x64.gz"))

# framework/ART 启动硬依赖的 APEX（依据：boot classpath 里的 libart/libc 与 ICU/TLS provider
# 都从 /apex 取；statsd 是 libs 的 statsd 客户端连的服务）。列出来是为了能求差集，不是猜。
REQUIRED = {
    "com.android.runtime": "linker + bionic 动态库（/apex/com.android.runtime/lib64/bionic）",
    "com.android.art": "libart + ART boot classpath + app_process 的 apex 版",
    "com.android.i18n": "ICU4C（java.text / ICU 数据）",
    "com.android.tzdata": "时区表（java.util.TimeZone）",
    "com.android.conscrypt": "TLS provider（framework 的 javax.net.ssl）",
    "com.android.os.statsd": "statsd 客户端/服务（framework 上报）",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=DEFAULT_IMG, help="initrd(.gz/.cpio) 或已挂载目录")
    ap.add_argument("--prefix", default="", help="目录模式给条目加的前缀（如 system/）")
    a = ap.parse_args()

    want = lambda n: n.endswith("apex-info-list.xml") or "/apex/" in "/" + n
    ents, fp = C.load(a.source, want=want, prefix=a.prefix)
    print("== audit_apex ==")
    print("来源: %s" % fp)
    print("条目总数: %d" % len(ents))

    # 1) 镜像里 apex 的三种形态分别统计
    active = {}      # /apex/<id>/**  —— 已解包（可直接当目录用）
    inactive = {}    # /system/apex/<id>.apex 或 system/apex/<id>/** —— 未激活副本
    apex_files = []  # 真正的 .apex 容器文件（要 apexd + loop 才能挂）
    for e in ents:
        n = e.name
        m = re.search(r"(?:^|/)apex/([^/]+)/(.+)$", n)
        if m and not n.startswith("system/apex/") and "/system/apex/" not in n:
            d = active.setdefault(m.group(1), [0, 0])
            if C.is_reg(e.mode):
                d[0] += 1
                d[1] += e.size
        if m and (n.startswith("system/apex/") or "/system/apex/" in n):
            d = inactive.setdefault(m.group(1), [0, 0])
            if C.is_reg(e.mode):
                d[0] += 1
                d[1] += e.size
        if n.endswith(".apex"):
            apex_files.append((n, e.size))

    C.table([(k, v[0], "%d KiB" % (v[1] // 1024), "✔ 必需" if k in REQUIRED else "可选",
              REQUIRED.get(k, "")) for k, v in sorted(active.items())],
            ["apex 实例", "文件数", "字节", "框架", "为什么需要"],
            "已解包在 apex/<id>/ 下的实例（= 不需要 loop 挂载就有内容）")
    C.table([(k, v[0], "%d KiB" % (v[1] // 1024)) for k, v in sorted(inactive.items())],
            ["apex 实例", "文件数", "字节"], "system/apex/ 下的副本")
    C.table([(n, "%d KiB" % (s // 1024)) for n, s in apex_files[:20]],
            [".apex 容器", "字节"], "需要 apexd+loop 才能挂载的容器文件")

    missing = sorted(set(REQUIRED) - set(active))
    print("\n结论:")
    print("  必需 APEX %d 个；镜像里已解包 %d 个；缺 %s" %
          (len(REQUIRED), len(set(REQUIRED) & set(active)), missing or "无"))
    print("  .apex 容器文件 %d 个 ⇒ %s" % (len(apex_files),
          "内容已是目录形态，apexd 的挂载职责可以绕过" if not apex_files else "必须走 apexd 挂载"))

    # 2) apexd 与它需要的东西（loop/dm）在不在
    have = {e.name: e for e in ents}
    for probe in ["system/bin/apexd", "system/bin/toybox", "system/bin/losetup",
                  "system/bin/dmctl", "system/etc/apex-info-list.xml"]:
        alt = [k for k in have if k.endswith(os.path.basename(probe))]
        print("  %-34s %s" % (probe, ("在（%s）" % alt[0]) if alt else "缺"))
    rc = [e for e in C.rc_files(ents) if "apexd" in e.name]
    print("  apexd 的 rc: %s" % ([e.name for e in rc] or "缺"))
    for e in rc:
        if e.data:
            svc = re.findall(r"(?m)^service\s+(\S+)\s+(\S+)", e.text)
            print("     服务: %s" % svc)
    # 3) apex-info-list.xml（init 的 apex 状态账本；属性名是 moduleName 不是 package）
    for e in ents:
        if e.name.endswith("apex-info-list.xml") and e.data:
            ids = re.findall(r'moduleName="([^"]+)"[^>]*isActive="(\w+)"', e.text)
            print("  apex-info-list.xml: %dB，记录 %d 个 apex ⇒ %s" %
                  (e.size, len(ids), ["%s(%s)" % (m, a) for m, a in ids][:8]))
    # 4) linker 是否已经按 /apex 找库（决定"解包目录"这条路能不能直接被 framework 用上）
    lc = [e for e in ents if e.name.startswith("linkerconfig") or e.name.endswith("ld.config.txt")]
    print("  linkerconfig 条目: %s" % [e.name for e in lc][:4])
    for e in lc:
        if e.data and b"/apex/" in e.data:
            print("     %s 里引用 /apex/ 路径 %d 次 ⇒ 动态链接已经在按 apex 目录找库"
                  % (e.name, e.data.count(b"/apex/")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
