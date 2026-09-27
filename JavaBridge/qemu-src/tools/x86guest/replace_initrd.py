#!/usr/bin/env python3
"""替换 cpio.gz（newc）initrd 里的指定文件，其余条目录样复制（流式重打包）。

用途（2026-09-27）：aarch64 ART guest 的 initrd 里 gb.dex 需要换成带 fetch op 的新版
（探壳流服务 6678 用），但不重跑 mk_art_initrd.py（需全套参数）。

用法：python replace_initrd.py <src.gz> <dst.gz> <文件名>=<新文件路径> [更多…]
示例：python replace_initrd.py in.gz out.gz gb.dex=..\\art\\gb.dex
"""
import gzip
import os
import sys


def main():
    src, dst = sys.argv[1], sys.argv[2]
    repl = {}
    for a in sys.argv[3:]:
        k, _, v = a.partition("=")
        with open(v, "rb") as f:
            repl[k] = f.read()
    done = set()
    n = 0
    with gzip.open(src, "rb") as f, gzip.open(dst, "wb", compresslevel=1) as o:
        while True:
            hdr = f.read(110)
            if len(hdr) < 110:
                break
            if hdr[:6] != b"070701":
                raise SystemExit("非 newc header @条目 %d" % n)
            filesize = int(hdr[54:62], 16)
            namesize = int(hdr[94:102], 16)
            name = f.read(namesize).rstrip(b"\0").decode("utf-8", "replace")
            pad = (-(110 + namesize)) % 4
            if pad:
                f.read(pad)
            n += 1
            if name == "TRAILER!!!":
                o.write(hdr)
                o.write(name.encode() + b"\0")
                if pad:
                    o.write(b"\0" * pad)
                break
            data = f.read(filesize)
            pad2 = (-filesize) % 4
            if pad2:
                f.read(pad2)
            base = os.path.basename(name)
            if base in repl:
                data = repl[base]
                done.add(base)
                h = bytearray(hdr)
                h[54:62] = ("%08X" % len(data)).encode()
                hdr = bytes(h)
                print("替换 %s: %d → %d B" % (base, filesize, len(data)))
            o.write(hdr)
            o.write(name.encode() + b"\0")
            if pad:
                o.write(b"\0" * pad)
            o.write(data)
            if (-len(data)) % 4:
                o.write(b"\0" * ((-len(data)) % 4))
    print("共 %d 条目，替换 %d/%d" % (n, len(done), len(repl)))
    miss = set(repl) - done
    if miss:
        print("警告：没找到 %s" % ", ".join(miss))
        sys.exit(2)


if __name__ == "__main__":
    main()
