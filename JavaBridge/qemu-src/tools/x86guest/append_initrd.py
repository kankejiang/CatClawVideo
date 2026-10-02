#!/usr/bin/env python3
"""向 cpio.gz(newc) initrd **追加**新条目（在 TRAILER 之前写入），其余条目录样复制。

用法：python append_initrd.py <src.gz> <dst.gz> <cpio内路径>=<本地文件> [...]
例：  python append_initrd.py in.gz out.gz modules/ext4.ko=.zwork/kmod/fs/ext4/ext4.ko

⚠ 与 replace_initrd.py 互补：那个只能替换已存在条目，这个用于新增。
   路径按 cpio 内相对名（不带前导 ./ 或 /），逐条去重命名冲突不检查（调用方负责）。
"""
import gzip, os, sys

def entry(name, data):
    n = name.encode()
    h = bytearray(b"070701" + b"0" * 104)
    def put(off, val, width):
        h[off:off + width] = ("%0*X" % (width, val)).encode()
    put(6, 1, 8)             # ino
    put(14, 0o100644, 8)     # mode
    put(22, 0, 8)            # uid
    put(30, 0, 8)            # gid
    put(38, 1, 8)            # nlink
    put(46, 0, 8)            # mtime
    put(54, len(data), 8)    # filesize
    put(62, 0, 8)            # devmajor
    put(70, 0, 8)            # devminor
    put(78, 0, 8)            # rdevmajor
    put(86, 0, 8)            # rdevminor
    put(94, len(n) + 1, 8)   # namesize
    put(102, 0, 8)           # check
    out = bytes(h) + n + b"\0"
    out += b"\0" * ((-len(out)) % 4)
    out += data
    out += b"\0" * ((-len(data)) % 4)
    return out

def main():
    src, dst = sys.argv[1], sys.argv[2]
    adds = []
    for a in sys.argv[3:]:
        k, _, v = a.partition("=")
        with open(v, "rb") as f:
            adds.append((k.strip("/"), f.read()))
    n = 0
    with gzip.open(src, "rb") as f, gzip.open(dst, "wb", compresslevel=1) as o:
        while True:
            hdr = f.read(110)
            if len(hdr) < 110:
                break
            if hdr[:6] != b"070701":
                raise SystemExit("非 newc header @条目 %d" % n)
            size = int(hdr[54:62], 16); nsize = int(hdr[94:102], 16)
            name = f.read(nsize).rstrip(b"\0").decode("utf-8", "replace")
            pad = (-(110 + nsize)) % 4
            if pad:
                f.read(pad)
            n += 1
            if name == "TRAILER!!!":
                for cname, data in adds:
                    o.write(entry(cname, data))
                    print("新增 %s (%d B)" % (cname, len(data)))
                o.write(hdr); o.write(b"TRAILER!!!" + b"\0")
                if pad:
                    o.write(b"\0" * pad)
                break
            data = f.read(size); pad2 = (-size) % 4
            if pad2:
                f.read(pad2)
            o.write(hdr); o.write(name.encode() + b"\0")
            if pad:
                o.write(b"\0" * pad)
            o.write(data)
            if pad2:
                o.write(b"\0" * pad2)
    print("共 %d 条目，新增 %d 条" % (n, len(adds)))

if __name__ == "__main__":
    main()
