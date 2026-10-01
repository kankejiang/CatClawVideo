#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""T4 探针共用的**只读**镜像/目录扫描器。

为什么自己写：要审计的对象是 298 MB 的 gzip cpio（我们的 guest initrd）和 108 上挂载的
Waydroid `system.img`，两边必须用**同一套判据**才叫对照。所以这里只提供一个流式条目迭代器，
`want(name)` 决定哪些条目的内容真读回来 —— 整份镜像 778 MB，全读进内存跑四次探针没必要。

⚠ 纪律：本模块**只读**。不写镜像、不改 `JavaBridge/qemu-src/**`（那是 T1 的战场）。
"""
from __future__ import annotations

import gzip
import hashlib
import os
import re

HDR = 110                                   # newc 固定头长度（不含文件名）
MAGIC = (b"070701", b"070702")
OFF_MODE, OFF_SIZE, OFF_NAMESIZE = 14, 54, 94
S_IFMT, S_IFDIR, S_IFREG, S_IFLNK = 0o170000, 0o040000, 0o100000, 0o120000


def is_dir(mode: int) -> bool: return (mode & S_IFMT) == S_IFDIR
def is_reg(mode: int) -> bool: return (mode & S_IFMT) == S_IFREG
def is_link(mode: int) -> bool: return (mode & S_IFMT) == S_IFLNK
def perm(mode: int) -> str: return "%04o" % (mode & 0o7777)


def norm(name: str) -> str:
    """条目名归一：去 `./` 前缀与首尾 `/`（与 `.zwork/inject_dex.py` 同一套规则）。"""
    while name.startswith("./"):
        name = name[2:]
    return name.strip("/")


class Entry:
    __slots__ = ("name", "mode", "size", "data")

    def __init__(self, name, mode, size, data=None):
        self.name, self.mode, self.size, self.data = name, mode, size, data

    @property
    def text(self) -> str:
        return (self.data or b"").decode("utf-8", "replace")

    @property
    def parts(self):
        return self.name.split("/")

    def __repr__(self):
        return "<%s %s %dB%s>" % (self.name, perm(self.mode), self.size,
                                  "" if self.data is not None else " (未取内容)")


def iter_cpio(path, want=None, max_want=24 * 1024 * 1024):
    """流式解析 newc cpio（`.gz` 或裸文件）。

    ⚠ 对齐规则必须按**绝对偏移**算（`HDR+namesize` 与 `HDR+namesize+size` 各自补到 4 的倍数），
    按条目内相对偏移算会在第一条之后就走歪 —— 症状是"只解析出 1 个条目"且不报错。
    """
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rb") as f:
        buf = bytearray()
        taken = 0

        def need(n):
            while len(buf) < n:
                chunk = f.read(1 << 20)
                if not chunk:
                    return False
                buf.extend(chunk)
            return True

        while True:
            if not need(HDR) or bytes(buf[:6]) not in MAGIC:
                return
            mode = int(buf[OFF_MODE:OFF_MODE + 8], 16)
            size = int(buf[OFF_SIZE:OFF_SIZE + 8], 16)
            ns = int(buf[OFF_NAMESIZE:OFF_NAMESIZE + 8], 16)
            if not need(HDR + ns):
                return
            name = norm(buf[HDR:HDR + ns - 1].decode("utf-8", "replace"))
            body = HDR + ns
            body += (4 - (body % 4)) % 4
            if not need(body + size):
                return
            take = bool(want) and size > 0 and want(name) and taken + size <= max_want
            data = bytes(buf[body:body + size]) if take else None
            if take:
                taken += size
            used = body + size
            used += (4 - (used % 4)) % 4
            del buf[:used]
            if name == "TRAILER!!!":
                return
            yield Entry(name, mode, size, data)


def iter_dir(root, want=None, prefix=""):
    """把一棵已挂载/解包的目录树当作镜像来迭代（108 上 `mount -o ro,loop` 后走这条路）。"""
    for dp, dns, fns in os.walk(root):
        for fn in sorted(fns):
            full = os.path.join(dp, fn)
            rel = norm(os.path.relpath(full, root)).replace("\\", "/")
            name = prefix + rel if prefix else rel
            st = os.lstat(full)
            link = os.readlink(full) if os.path.islink(full) else None
            size = st.st_size
            take = bool(want) and want(name)
            data = None
            if take and size and size <= 24 * 1024 * 1024:
                try:
                    with open(full, "rb") as fh:
                        data = fh.read()
                except OSError:
                    data = None
            yield Entry(name, stat_mode(st), size, data)


def stat_mode(st) -> int:
    import stat as s
    m = st.st_mode & S_IFMT
    if m == s.S_IFDIR: return 0o040000 | (st.st_mode & 0o7777)
    if m == s.S_IFLNK: return 0o120000 | (st.st_mode & 0o7777)
    return 0o100000 | (st.st_mode & 0o7777)


def load(source, want=None, prefix=""):
    """统一入口：`source` 是镜像文件（.gz/.cpio）或目录 → 返回条目列表 + 来源指纹。"""
    if os.path.isdir(source):
        ents = list(iter_dir(source, want, prefix))
        fp = "dir:" + os.path.abspath(source).replace("\\", "/")
    else:
        ents = list(iter_cpio(source, want))
        fp = "file:%s sha256=%s size=%d" % (os.path.basename(source), sha256_file(source)[:16],
                                            os.path.getsize(source))
    return ents, fp


def sha256_file(path, cap=None) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        read = f.read if cap is None else (lambda n, f=f: f.read(min(n, cap)))
        while True:
            b = f.read(1 << 20) if cap is None else f.read(cap)
            if not b:
                break
            h.update(b)
            if cap is not None:
                break
    return h.hexdigest()


def head(ents, names):
    """打印一段人类可读的小节标题（探针输出统一走这个格式，便于贴进文档）。"""
    print("\n── %s ──" % names)


def table(rows, cols, title=""):
    """极简表格输出（不引第三方库）。rows = list[tuple]。"""
    if title:
        print("\n── %s ──" % title)
    if not rows:
        print("   （无）")
        return
    widths = [max(len(str(c)) for c in [cols[i]] + [r[i] for r in rows]) for i in range(len(cols))]
    line = "   " + "  ".join(str(c).ljust(widths[i]) for i, c in enumerate(cols))
    print(line)
    print("   " + "-" * (len(line) - 3))
    for r in rows:
        print("   " + "  ".join(str(v).ljust(widths[i]) for i, v in enumerate(r)))


def by_name(ents):
    return {e.name: e for e in ents}


RC_NAME = re.compile(r"\.rc$")


def rc_files(ents):
    """镜像里的 init rc：`system/etc/init/**.rc`（含 `hw/` 子目录）与 `system/etc/init/hw/*.rc`。"""
    return [e for e in ents if RC_NAME.search(e.name) and "/etc/init" in e.name]
