#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""packer_drift —— 打包器/关键脚本"多副本漂移"检查（只读，零副作用）。

为什么需要它（2026-10-02 实证）：
  同一份打包逻辑在本仓库里存在**三处副本** ✗：
    * JavaBridge/qemu-src/tools/x86guest/pack_android_stack.py   （仓库版，rebuild 链里被引用 ✗）
    * .zwork/b11_astack.py                                        （历史上真正跑的那份 ✗）
    * tools/b1-build/b11_astack.py                                 （交接时入仓库的权威副本 ✔）
  三者内容**互不相同** ✗（例如显式 `libicu` 的行数 0 / 0 / 5 ✗），而"跑哪一份"取决于
  当时谁 scp 了哪份到 108 ✗ ⇒ 于是出现"修复写了、镜像里却没有" ✗。
  这个脚本只**检查并报告** ✗，不改任何文件 ✗，可随时安全运行 ✔。

用法：
  python tools/triage/packer_drift.py            # 人类可读
  python tools/triage/packer_drift.py --json     # 机器可读
退出码：0 = 无漂移；1 = 有漂移；2 = 用法/路径错误。
"""

import argparse
import hashlib
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# 每个逻辑脚本 -> (候选路径, 该脚本里"必须出现"的关键标记)
GROUPS = [
    {
        "name": "astack packer",
        "candidates": [
            "JavaBridge/qemu-src/tools/x86guest/pack_android_stack.py",
            ".zwork/b11_astack.py",
            "tools/b1-build/b11_astack.py",
        ],
        "markers": ["libicu", "libharfbuzz", "libminigbm"],
    },
    {
        "name": "mesa packer",
        "candidates": [
            "JavaBridge/qemu-src/tools/x86guest/pack_mesa_gl.py",
            ".zwork/b11_mesa_pack.py",
            "tools/b1-build/b11_mesa_pack.py",
        ],
        "markers": ["libgallium_dri.so", "libgbm_mesa.so", "dri_gbm.so"],
    },
    {
        "name": "init / bootimg builder",
        "candidates": [
            "JavaBridge/qemu-src/tools/x86guest/build_bootimg_inject.py",
            ".zwork/build_bootimg_inject.py",
            "tools/b1-build/build_bootimg_inject.py",
        ],
        "markers": ["gralloc.gbm.device", "libgallium_dri.so", "hal_ready"],
    },
]


def sha12(path):
    h = hashlib.sha256()
    with io.open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()[:12]


def marker_counts(path, markers):
    try:
        with io.open(path, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return {}
    return {m: text.count(m) for m in markers}


def main():
    ap = argparse.ArgumentParser(description="packer copy drift check (read-only)")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    report = []
    drifted = False
    for g in GROUPS:
        entries = []
        for rel in g["candidates"]:
            p = os.path.join(ROOT, rel.replace("/", os.sep))
            if os.path.isfile(p):
                entries.append({
                    "path": rel,
                    "size": os.path.getsize(p),
                    "sha12": sha12(p),
                    "markers": marker_counts(p, g["markers"]),
                })
            else:
                entries.append({"path": rel, "missing": True})
        present = [e for e in entries if "missing" not in e]
        hashes = {e["sha12"] for e in present}
        group_drift = len(hashes) > 1
        if group_drift:
            drifted = True
        report.append({"group": g["name"], "markers": g["markers"],
                       "entries": entries, "drift": group_drift})

    if a.json:
        print(json.dumps({"drift": drifted, "groups": report}, ensure_ascii=True, indent=1))
        return 1 if drifted else 0

    print("=" * 78)
    print("packer / script copy drift check  (read-only)")
    print("=" * 78)
    for r in report:
        print()
        print("[%s]  %s" % ("DRIFT" if r["drift"] else "OK   ", r["group"]))
        for e in r["entries"]:
            if "missing" in e:
                print("   %-56s  (absent)" % e["path"])
            else:
                mk = " ".join("%s=%d" % (k, v) for k, v in e["markers"].items())
                print("   %-56s %8d B  %s  %s" % (e["path"], e["size"], e["sha12"], mk))
    print()
    if drifted:
        print("VERDICT: DRIFT DETECTED -- the same logic exists in several copies that differ.")
        print("  => Decide ONE authoritative path (recommend tools/b1-build/), make the others")
        print("     generated copies, and print the chosen path + hash in the build output.")
        print("  => Until then, 'I fixed it but the image did not change' is expected, not surprising.")
    else:
        print("VERDICT: no drift (all copies identical).")
    return 1 if drifted else 0


if __name__ == "__main__":
    sys.exit(main())
