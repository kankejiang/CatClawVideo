#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""boot_diag —— 只看**某一次开机**的失败原因（ASCII 输出，read-only）。

背景：日志已 2.2GB / 255 次开机，逐条 grep 必然命中旧开机 ⇒ 必须先切分。
用法：
  python tools/triage/boot_diag.py            # 最后一次开机
  python tools/triage/boot_diag.py --boot 2   # 倒数第 2 次
  python tools/triage/boot_diag.py --boot 4 --lines 400
"""
import argparse, io, os, re, sys

LOG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "CatClawVideo.debug",
                   "qemu-console-art.log")

# 只关心"失败"相关
PATTERNS = [
    "panic", "Kernel panic", "Oops", "BUG:", "Call Trace", "end Kernel",
    "cannot open", "No such file or directory", "not found", "denied",
    "Read-only file system", "Out of memory", "oom-killer", "killed process",
    "init: ", "Service '", "failed", "Failed", "FAIL", "Aborting", "abort",
    "Fatal signal", "signal 11", "signal 6", "Segment", "Assertion",
    "apex", "zygote", "surfaceflinger", "servicemanager", "hwservicemanager",
    "binder", "drm", "gbm", "egl", "vulkan", "lavapipe", "shm", "tmpfs",
    "mount", "EXT4", "ext4", "unexpected error", "error:",
    "QEMU", "qemu", "guest agent", "blacklist",
]
KERNEL_TS = re.compile(r"^\[\s*([0-9]+\.[0-9]+)\]")


def boots(path):
    out = []
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        off = 0
        for line in f:
            if "===== QEMU" in line and "\u542f\u52a8" in line:
                m = re.search(r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})", line)
                out.append((off, m.group(1) if m else line.strip()))
            off += len(line.encode("utf-8", "replace"))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=LOG)
    ap.add_argument("--boot", default="last")
    ap.add_argument("--lines", type=int, default=300, help="tail lines to show")
    ap.add_argument("--show", default="kernel,error", help="grep groups: kernel,error,service,gpu,all")
    a = ap.parse_args()

    if not os.path.isfile(a.log):
        sys.stderr.write("log missing\n"); return 2
    bs = boots(a.log)
    if not bs:
        sys.stderr.write("no boots\n"); return 2
    n = 1 if a.boot == "last" else int(a.boot)
    i = len(bs) - n
    so = bs[i][0]
    eo = bs[i + 1][0] if i + 1 < len(bs) else None

    groups = {
        "kernel": [r"\[\s*\d+\.\d+\]", r"panic", r"Oops", r"Call Trace", r"end Kernel"],
        "error": [r"not found", r"No such file", r"denied", r"Read-only", r"failed",
                  r"Failed", r"error", r"Error", r"ERROR", r"abort", r"Fatal", r"signal \d+"],
        "service": [r"init: ", r"Service '", r"zygote", r"apexd", r"apex",
                    r"surfaceflinger", r"SurfaceFlinger", r"servicemanager",
                    r"binder", r"zygote"],
        "gpu": [r"drm", r"gbm", r"egl", r"EGL", r"vulkan", r"lavapipe", r"gralloc",
                r"minigbm", r"virtio"],
    }
    sel = a.show.split(",")
    pats = []
    if "all" in sel:
        pats = PATTERNS
    else:
        for g in sel:
            pats += groups.get(g.strip(), [])
    rx = re.compile("|".join(pats)) if pats else None

    keep, last_t = [], 0.0
    pos = so
    with io.open(a.log, "r", encoding="utf-8", errors="replace") as f:
        f.seek(so)
        for line in f:
            if eo is not None and pos >= eo:
                break
            pos += len(line.encode("utf-8", "replace"))
            m = KERNEL_TS.match(line)
            if m:
                try:
                    v = float(m.group(1))
                    if v > last_t:
                        last_t = v
                except ValueError:
                    pass
            if rx is None or rx.search(line):
                keep.append(line.rstrip())

    print("=" * 70)
    print("boot #%d from newest @ %s" % (n, bs[i][1]))
    print("matched %d lines   last kernel ts = %.2fs" % (len(keep), last_t))
    print("=" * 70)
    for s in keep[-a.lines:]:
        print("  " + s.encode("ascii", "replace").decode()[:170])
    if not keep:
        print("  (no matched lines in this boot)")
    return 0


if __name__ == "__main__":
    sys.exit(main())