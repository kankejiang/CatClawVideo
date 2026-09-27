#!/usr/bin/env python3
"""从 initrd（cpio.gz, newc 格式）流式提取指定文件。

用途（2026-09-27）：对「用户环境 initrd（部署目录，壳全链路活）」与「108 现组装」
做物证对比 —— 提取 gb.dex / ui_stub.dex 看类集合差异（VerifyError 排查）。

用法：python extract_initrd.py <initrd.gz> <输出目录> [要提取的文件名…]
"""
import gzip
import os
import sys


def main():
    src, outdir = sys.argv[1], sys.argv[2]
    args = [a for a in sys.argv[3:] if a != "--list"]
    listing = "--list" in sys.argv
    want = args or None
    os.makedirs(outdir, exist_ok=True)
    total = 0
    seen = 0
    with gzip.open(src, "rb") as f:
        while True:
            hdr = f.read(110)
            if len(hdr) < 110:
                break
            if hdr[:6] != b"070701":
                print("非 newc header，停止（偏移 %d）" % total)
                break
            total += 110
            filesize = int(hdr[54:62], 16)
            namesize = int(hdr[94:102], 16)
            name = f.read(namesize).rstrip(b"\0").decode("utf-8", "replace")
            pad = (-(110 + namesize)) % 4
            if pad:
                f.read(pad)
            total += namesize + pad
            if name == "TRAILER!!!":
                break
            data = f.read(filesize)
            pad2 = (-filesize) % 4
            if pad2:
                f.read(pad2)
            total += filesize + pad2
            base = os.path.basename(name)
            seen += 1
            if listing:
                if not want or any(w in name for w in want):
                    print("%5d %8d %s" % (seen, filesize, name))
                continue
            if not want or any(w in base for w in want):
                p = os.path.join(outdir, base)
                with open(p, "wb") as o:
                    o.write(data)
                print("提取 %-20s → %s（%dB）" % (base, p, len(data)))
    print("共 %d 个条目，扫描 %d 字节" % (seen, total))


if __name__ == "__main__":
    main()
