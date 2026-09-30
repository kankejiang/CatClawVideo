#!/usr/bin/env python3
"""替换 cpio.gz（newc）initrd 里的指定文件，其余条目录样复制（流式重打包）。

用途（2026-09-27）：aarch64 ART guest 的 initrd 里 gb.dex 需要换成带 fetch op 的新版
（探壳流服务 6678 用），但不重跑 mk_art_initrd.py（需全套参数）。

用法：python replace_initrd.py <src.gz> <dst.gz> <条目路径>=<新文件路径> [更多…]
示例：python replace_initrd.py in.gz out.gz gb.dex=..\\art\\gb.dex
      python replace_initrd.py in.gz out.gz init=.zwork/init_now.sh

⚠️ 匹配用**归一后的完整路径**，绝不可用 os.path.basename（2026-09-30 实测事故）：
   basename 匹配下 key=init 会同时命中三个条目 ——
       init            （cpio 根的引导脚本，本意要换的那个）
       system/bin/init （Android 真 init 二进制，2.3MB → 被写成 7KB shell 文本，条目被毁）
       system/etc/init （目录条目 → body 被塞进 7KB）
   key=gb.dex 同理还会命中 data/dalvik-cache/x86_64/gb.dex（dex2oat 缓存产物）。
   三者都不报错：镜像仍能启动（PID1 是 cpio 根的 /init），属于「静默毁产物」。
   自检见 tools/x86guest/check_image.py（已含对应断言）。
"""
import gzip
import os
import sys


def norm_path(n: str) -> str:
    """条目名归一：去 './' 前缀与首尾 '/'，用于**精确**匹配。"""
    while n.startswith("./"):
        n = n[2:]
    return n.strip("/")


def main():
    src, dst = sys.argv[1], sys.argv[2]
    repl = {}
    for a in sys.argv[3:]:
        k, _, v = a.partition("=")
        with open(v, "rb") as f:
            repl[norm_path(k)] = f.read()
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
            key = norm_path(name)
            if key in repl:
                data = repl[key]
                done.add(key)
                h = bytearray(hdr)
                h[54:62] = ("%08X" % len(data)).encode()
                hdr = bytes(h)
                # 打印**完整条目名**：一旦有人改回 basename 匹配，日志里会立刻看到
                # 多出来的 system/bin/init、system/etc/init，而不是只看到一个 init
                print("替换 %s: %d → %d B" % (name, filesize, len(data)))
            o.write(hdr)
            o.write(name.encode() + b"\0")
            if pad:
                o.write(b"\0" * pad)
            o.write(data)
            if (-len(data)) % 4:
                o.write(b"\0" * ((-len(data)) % 4))
    print("共 %d 条目，替换 %d 条（请求 %d）" % (n, len(done), len(repl)))
    miss = set(repl) - done
    if miss:
        print("警告：没找到 %s" % ", ".join(sorted(miss)))
        sys.exit(2)


if __name__ == "__main__":
    main()
