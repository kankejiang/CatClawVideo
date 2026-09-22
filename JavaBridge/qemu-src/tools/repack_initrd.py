#!/usr/bin/env python3
"""重打包 pkg_initrd.gz（cpio newc + gzip）—— 改 guest 的 /init 等文本件而不重编任何二进制。

为什么需要它：`/init` 本体是 busybox 脚本（不是 C 代码），挂在 `ThunderRuntime/pkg_initrd.gz` 里。
仓库里的 patch_init.py / harness4.c 与**已分发产物并不一致**（例如 tmpfs 挂载只存在于 initrd 里），
所以改 guest 引导行为只能改 initrd 本体。

用法::

    # 把所有条目解出来（目录 + 各自文件），便于 diff 与存档
    python repack_initrd.py dump  <initrd.gz> <outdir>

    # 用本地文件替换归档里的某个条目，另存为新 initrd（不指定 --out 就就地覆盖）
    python repack_initrd.py replace <initrd.gz> init init.sh [--out new.gz]

    # 只看条目清单
    python repack_initrd.py list  <initrd.gz>

⚠ cpio newc 的对齐规则：**固定头(110B) + 名字长** 一起向上取整到 4，
  不是只对齐名字长。只对齐名字长会从第二个条目起解析错位（实测只解出 1 条）。
"""

import gzip
import io
import os
import sys

MAGICS = (b"070701", b"070702")


def read_cpio(data):
    """返回 [(name, mode, uid, gid, nlink, mtime, devmaj, devmin, rmaj, rmin, body_bytes)]。"""
    ents, off = [], 0
    while off + 110 <= len(data):
        if data[off:off + 6] not in MAGICS:
            break
        f = lambda i: int(data[off + 6 + i * 8: off + 6 + i * 8 + 8], 16)
        mode, uid, gid, nlink = f(1), f(2), f(3), f(4)
        mtime, size = f(5), f(6)
        devmaj, devmin, rmaj, rmin, namesize = f(7), f(8), f(9), f(10), f(11)
        name = data[off + 110: off + 110 + namesize - 1].decode("utf-8", "replace")
        if name == "TRAILER!!!":
            break
        doff = off + ((110 + namesize + 3) & ~3)
        ents.append((name, mode, uid, gid, nlink, mtime, devmaj, devmin, rmaj, rmin,
                     data[doff:doff + size]))
        nxt = doff + ((size + 3) & ~3)
        if nxt <= off:
            raise SystemExit("cpio 解析失去前进（%d）" % off)
        off = nxt
    return ents


def write_cpio(ents):
    out = io.BytesIO()
    for i, (name, mode, uid, gid, nlink, mtime, devmaj, devmin, rmaj, rmin, body) in enumerate(ents):
        nb = name.encode()
        hdr = b"070701" + b"".join(
            b"%08X" % v for v in (i + 1, mode, uid, gid, nlink, mtime, len(body),
                                  devmaj, devmin, rmaj, rmin, len(nb) + 1))
        hdr += b"00000000"          # check：newc 恒为 0
        assert len(hdr) == 110, len(hdr)
        out.write(hdr)
        out.write(nb + b"\x00")
        out.write(b"\x00" * (((110 + len(nb) + 1 + 3) & ~3) - (110 + len(nb) + 1)))
        out.write(body)
        out.write(b"\x00" * (((len(body) + 3) & ~3) - len(body)))
    # ⚠ TRAILER 也必须是**完整 110 字节头**（6 魔术 + 13×8 字段 = 110）。
    #   少写一个字段（102 字节）会解析出 37 个条目 —— 内核 cpio 解析器同样会错位，
    #   可能找不到 TRAILER 而报 initramfs 损坏。
    nb = b"TRAILER!!!"
    hdr = b"070701" + b"".join(
        b"%08X" % v for v in (len(ents) + 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, len(nb) + 1))
    hdr += b"00000000"
    assert len(hdr) == 110, len(hdr)
    out.write(hdr)
    out.write(nb + b"\x00")
    out.write(b"\x00" * (((110 + len(nb) + 1 + 3) & ~3) - (110 + len(nb) + 1)))
    return out.getvalue()


def load(path):
    return read_cpio(gzip.decompress(open(path, "rb").read()))


def save(ents, path):
    raw = write_cpio(ents)
    # mtime=0：产物可复现（内容决定字节，不因打包时间而变）
    with open(path, "wb") as f:
        f.write(gzip.compress(raw, 9, mtime=0))
    return len(raw)


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    cmd, src = argv[1], argv[2]

    if cmd == "list":
        for e in load(src):
            kind = "dir " if e[1] & 0o40000 else "file"
            print("  %-34s %s mode=%06o %10d B" % (e[0], kind, e[1] & 0o7777, len(e[10])))
        return 0

    if cmd == "dump":
        outdir = argv[3]
        for name, mode, uid, gid, nlink, mtime, dmaj, dmin, rmaj, rmin, body in load(src):
            p = os.path.join(outdir, name.lstrip("/"))
            if mode & 0o40000:
                os.makedirs(p, exist_ok=True)
            else:
                os.makedirs(os.path.dirname(p) or ".", exist_ok=True)
                with open(p, "wb") as f:
                    f.write(body)
                os.chmod(p, mode & 0o7777)
        print("已解出到 %s" % outdir)
        return 0

    if cmd == "replace":
        target, newfile = argv[3], argv[4]
        outp = src
        if "--out" in argv:
            outp = argv[argv.index("--out") + 1]
        ents = load(src)
        body = open(newfile, "rb").read()
        if "--lf" in argv:
            # ⚠ 本仓 core.autocrlf 开着：.sh 一旦被 checkout 成 CRLF，写进 initrd 后
            #   busybox sh 会因为行尾 \r 直接崩。发布用的脚本必须 LF。
            n_cr = body.count(b"\r\n")
            body = body.replace(b"\r\n", b"\n")
            if n_cr:
                print("  --lf：已把 %d 处 CRLF 规范成 LF" % n_cr)
        hit = 0
        new = []
        for e in ents:
            if e[0] == target:
                # 保留 mode/owner/mtime，只换内容
                new.append(e[:10] + (body,))
                hit += 1
            else:
                new.append(e)
        if hit != 1:
            raise SystemExit("归档里找不到唯一条目 %r（命中 %d 次）" % (target, hit))
        n = save(new, outp)
        print("已替换 %r：%d B → %d B；cpio %d B；写出 %s" % (target, len([e for e in ents if e[0] == target][0][10]), len(body), n, outp))
        return 0

    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
