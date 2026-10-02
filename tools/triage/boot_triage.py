#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""boot_triage —— B1 guest "单次开机"取证与判定（只读，零副作用）。

为什么需要它（2026-10-02 的教训）：
  * qemu-console-art.log 一天涨到 570MB+、开机 170+ 次。
    直接取"最后一条匹配"极易命中**旧开机**，得出错误结论（今天发生过多次）。
  * 输出一律 ASCII：往 GBK 控制台打印 ✔/✗/⇒ 会 UnicodeEncodeError（踩过）。
  * 只认"最后一次 `===== QEMU 启动`"之后的段落，这是唯一可信的证据区间。

用法：
  python tools/triage/boot_triage.py                 # 分析最后一次开机
  python tools/triage/boot_triage.py --boot 3        # 倒数第 3 次开机
  python tools/triage/boot_triage.py --all-boots     # 列出最近若干次开机时间线
  python tools/triage/boot_triage.py --json          # 机器可读输出

退出码：0 = P0 闸门通过（分配 + SF 启动 + PNG 魔数）；1 = 有闸门未过；2 = 用法/文件错误。
"""

import argparse
import io
import json
import os
import re
import sys

DEFAULT_LOG = os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "CatClawVideo.debug", "qemu-console-art.log"
)

MARKERS = {
    # 名称: 子串
    "boot_marker": "===== QEMU",
    "sf_starting": "SurfaceFlinger is starting",
    "renderengine": "RenderEngine with SkiaGL",
    "sf_line": "[sf]",
    "alloc_in": ">> allocate(",
    "alloc_ok": "<< allocate(9) rc=0",
    "alloc_5": "rc=5",
    "alloc_7": "rc=7",
    "gbm_create": "gbm_create_device(",
    "gbm_bo": "gbm_bo_create(",
    "gbm_permission": "Permission denied",
    "png_magic": "89504E47",
    "init_svc": "init.svc.",
    "zygote_kill": "Sending signal 9 to service 'zygote'",
    "zygote_ok": "init.svc.zygote=running",
    "propinit": "propinit",
    "ebadf": "EBADF",
    "area_init_fail": "area_init",
    "libicu": "libicu",
    "cannot_link": "CANNOT LINK EXECUTABLE",
    "aidl_lazy": "SurfaceFlingerAIDL",
    "dri_symlink_echo": "dri ",
    "hal_ready": "hal_ready",
    "screencap_rc124": "rc=124",
    "crashed": "\u6536\u5230\u4fe1\u53f7",  # 收到信号
    "verify": "[verify]",
}


def find_boots(path):
    """两遍流式扫描，返回 [(offset, line)]（不把文件读进内存）。"""
    boots = []
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        off = 0
        for line in f:
            if "===== QEMU" in line and "\u542f\u52a8" in line:
                t = line.strip().split("=====")[-2].strip()
                boots.append((off, "QEMU boot @ %s" % t.encode("ascii", "replace").decode()))
            off += len(line.encode("utf-8", "replace"))
    return boots


def scan_range(path, start_off, end_off=None):
    """扫描 [start_off, end_off) 区间，统计标记出现次数与最后一条样例。"""
    counts = {k: 0 for k in MARKERS}
    lasts = {k: "" for k in MARKERS}
    lines = 0
    last_t = 0.0
    t_re = re.compile(r"^\[ *([0-9]+\.[0-9]+)\]")
    pos = start_off
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        if start_off:
            f.seek(start_off)
        for line in f:
            # NOTE: f.tell() is disabled during text iteration; track offsets ourselves.
            if end_off is not None and pos >= end_off:
                break
            pos += len(line.encode("utf-8", "replace"))
            lines += 1
            mt = t_re.match(line)
            if mt:
                try:
                    v = float(mt.group(1))
                    if v > last_t:
                        last_t = v
                except ValueError:
                    pass
            s = line.rstrip("\n")
            for k, pat in MARKERS.items():
                if pat in s:
                    counts[k] += 1
                    lasts[k] = s.replace("[proppreload]", "").strip()[:200]
    return counts, lasts, lines, last_t


def gate_report(counts, lasts):
    """P0 / E2 闸门判定。"""
    gates = [
        ("P0.1 allocation succeeded (rc=0)", counts["alloc_ok"] > 0,
         "%d occurrences" % counts["alloc_ok"]),
        ("P0.2 SurfaceFlinger produced output", counts["sf_line"] > 0,
         "%d [sf] lines" % counts["sf_line"]),
        ("P0.3 SurfaceFlinger is starting", counts["sf_starting"] > 0,
         "%d occurrences" % counts["sf_starting"]),
        ("P0.4 screencap produced PNG magic", counts["png_magic"] > 0,
         "%d occurrences" % counts["png_magic"]),
        ("E2  real init property area (init.svc present)", counts["init_svc"] > 20,
         "%d init.svc lines" % counts["init_svc"]),
    ]
    return gates


def main():
    ap = argparse.ArgumentParser(description="B1 guest per-boot triage (read-only)")
    ap.add_argument("--log", default=DEFAULT_LOG, help="qemu console log path")
    ap.add_argument("--boot", default="last",
                    help="'last' (default) or N = the N-th newest boot (1 = newest)")
    ap.add_argument("--all-boots", action="store_true", help="list recent boot markers")
    ap.add_argument("--boots", type=int, default=6, help="how many boots to list")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--matrix", type=int, default=0,
                    help="compare the newest N boots across key gates (regression gate)")
    a = ap.parse_args()

    if not os.path.isfile(a.log):
        sys.stderr.write("log not found: %s\n" % a.log)
        return 2
    size_mb = os.path.getsize(a.log) / 1048576.0

    boots = find_boots(a.log)
    if not boots:
        sys.stderr.write("no boot marker found in %s\n" % a.log)
        return 2

    if a.matrix:
        n_m = min(a.matrix, len(boots))
        cols = [("sf_start", "SFstart"), ("sf_line", "sf-lines"), ("alloc_ok", "alloc=0"),
                ("png", "PNG"), ("init_svc", "init.svc"), ("aidl", "AIDLspam"),
                ("ebadf", "areaERR"), ("zyg_kill", "zyg-kill"), ("crash", "crash")]
        lookup = {"sf_start": "sf_starting", "sf_line": "sf_line", "alloc_ok": "alloc_ok",
                  "png": "png_magic", "init_svc": "init_svc", "aidl": "aidl_lazy",
                  "ebadf": "area_init_fail", "zyg_kill": "zygote_kill", "crash": "crashed"}
        print("log: %.1f MB   boots: %d   (newest %d)" % (size_mb, len(boots), n_m))
        print("  %-19s %-11s %7s %s" % ("boot", "mode", "alive", " ".join("%9s" % c[1] for c in cols)))
        for k in range(n_m, 0, -1):
            i = len(boots) - k
            so = boots[i][0]
            eo = boots[i + 1][0] if i + 1 < len(boots) else None
            c, _, _, lt = scan_range(a.log, so, eo)
            vals = []
            for key, _ in cols:
                v = c[lookup[key]]
                vals.append("%9d" % v)
            mode = "real-init" if c["init_svc"] > 20 else "claw-legacy"
            print("  %-19s %-11s %6.0fs %s" % (boots[i][1][-19:], mode, lt, " ".join(vals)))
        print()
        print("  reading: SFstart>0 = SF started; alloc=0>0 = allocation OK; PNG>0 = screencap OK;")
        print("           AIDLspam high with SFstart=0 => screencap will hang; areaERR>0 => getprop empty.")
        print("           mode: real-init = init.svc present (shadow/real init); claw-legacy = absent.")
        print("           NEVER compare results across modes -- they are different guests.")
        return 0

    if a.all_boots:
        print("log: %.1f MB   boots: %d" % (size_mb, len(boots)))
        for off, txt in boots[-a.boots:]:
            print("   %s" % txt.encode("ascii", "replace").decode())
        return 0

    try:
        n = 1 if a.boot == "last" else int(a.boot)
    except ValueError:
        sys.stderr.write("--boot must be 'last' or an integer\n")
        return 2
    if n < 1 or n > len(boots):
        sys.stderr.write("--boot %d out of range (1..%d)\n" % (n, len(boots)))
        return 2

    idx = len(boots) - n
    start_off, start_txt = boots[idx]
    end_off = boots[idx + 1][0] if idx + 1 < len(boots) else None
    counts, lasts, lines, last_t = scan_range(a.log, start_off, end_off)
    gates = gate_report(counts, lasts)
    passed = sum(1 for _, ok, _ in gates if ok)

    result = {
        "log_mb": round(size_mb, 1),
        "boot_count": len(boots),
        "boot": start_txt.encode("ascii", "replace").decode(),
        "boot_index_from_end": n,
        "lines_in_boot": lines,
        "counts": counts,
        "lasts": {k: v.encode("ascii", "replace").decode() for k, v in lasts.items()},
        "gates": [{"name": g, "pass": ok, "detail": d} for g, ok, d in gates],
        "gates_passed": passed,
        "gates_total": len(gates),
    }
    if a.json:
        print(json.dumps(result, ensure_ascii=True, indent=1))
        return 0 if passed == len(gates) else 1

    print("=" * 72)
    print("B1 boot triage  (read-only)")
    print("=" * 72)
    print("log          : %s" % a.log)
    print("log size     : %.1f MB   (large log => ALWAYS scope to one boot)" % size_mb)
    print("boot count   : %d" % len(boots))
    print("analyzed     : QEMU boot @ %s   (#%d from newest)" % (start_txt, n))
    print("lines in boot: %d" % lines)
    print()
    print("-- gates --")
    for g, ok, d in gates:
        print("  [%s] %-46s %s" % ("PASS" if ok else "FAIL", g, d))
    print("  => %d/%d gates passed" % (passed, len(gates)))
    print()
    print("-- key counters --")
    for k in ("sf_starting", "renderengine", "sf_line", "alloc_in", "alloc_ok",
              "init_svc", "zygote_kill", "zygote_ok", "libicu", "cannot_link",
              "aidl_lazy", "png_magic", "ebadf", "area_init_fail", "hal_ready",
              "crashed", "dri_symlink_echo"):
        print("  %-18s %6d   last: %s" % (k, counts[k], lasts[k][:110]))
    print()
    print("-- hints --")
    if counts["aidl_lazy"] > 0 and counts["sf_line"] == 0:
        print("  * SurfaceFlingerAIDL lazy-start spam with no [sf] output")
        print("    => SurfaceFlinger never started/failed silently; screencap will hang.")
    if counts["ebadf"] > 0 or counts["area_init_fail"] > 0:
        print("  * property area problem (area_init / EBADF) => getprop likely empty.")
    if counts["cannot_link"] > 0:
        print("  * linker CANNOT LINK => a needed .so is missing from the running image.")
    if counts["alloc_ok"] > 0 and counts["sf_line"] == 0:
        print("  * allocation worked but SF silent => regression in the SF launch step.")
    print("  * hygiene: ensure exactly ONE CatClawVideo.Maui and ONE qemu-system-x86_64")
    print("    process before trusting any conclusion (duplicates => wrong guest).")
    return 0 if passed == len(gates) else 1


if __name__ == "__main__":
    sys.exit(main())
