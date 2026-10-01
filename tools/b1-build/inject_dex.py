#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把新 gb.dex / ui_stub.dex / init / 任意文件 换进（或加入）现网 initrd（流式重写 cpio，其余条目原样）。

用法:
  python inject_dex.py <in.gz> <out.gz> gb.dex=<路径> [ui_stub.dex=<路径>] [init=<路径>] ...
  python inject_dex.py <in.gz> <out.gz> <镜像内路径>=-              # 删除该条目
  python inject_dex.py <in.gz> <out.gz> +0755:system/bin/adbd=<本地文件>   # **新增**条目（带权限）

⚠️ 匹配必须用**归一后的完整路径**，绝不能用 os.path.basename（2026-09-30 实测事故）：
   basename 匹配下，key=init 会同时命中三个条目 ——
       init            （cpio 根的引导脚本，本意要换的那个）
       system/bin/init （Android 真 init 二进制，2.3MB → 被写成 7KB shell 文本，条目被毁）
       system/etc/init （目录条目 → body 被塞进 7KB）
   同理 key=gb.dex 还会命中 data/dalvik-cache/x86_64/gb.dex（dex2oat 缓存产物）。
   症状：镜像仍能启动（PID1 是 cpio 根的 /init），但 system/bin/init 已被毁，
   且注入器**不会报任何错** —— 属于「静默毁产物」。已在 check_image.py 里加了对应断言。

2026-10-01 扩展：支持 `+<八进制权限>:<路径>=<本地文件>` **新增**条目（B1.0 注入 adbd 用）。
   新增条目会在 cpio 的 TRAILER!!! 之前写出（写在其后会被内核忽略）。
"""
import gzip, sys, io, os

# newc 头：magic(6) ino(8) mode(8) uid(8) gid(8) nlink(8) mtime(8) filesize(8)
#        devmaj(8) devmin(8) rdevmaj(8) rdevmin(8) namesize(8) check(8)
OFF_INO, OFF_MODE, OFF_UID, OFF_GID, OFF_NLINK, OFF_MTIME, OFF_SIZE = 6, 14, 22, 30, 38, 46, 54
OFF_DEVMAJ, OFF_DEVMIN, OFF_RDEVMAJ, OFF_RDEVMIN, OFF_NAMESIZE, OFF_CHECK = 62, 70, 78, 86, 94, 102
HDR = 110


def norm_path(n: str) -> str:
    """条目名归一：去 './' 前缀与首尾 '/'，用于**精确**匹配。"""
    while n.startswith("./"):
        n = n[2:]
    return n.strip("/")


src, dst = sys.argv[1], sys.argv[2]
repl = {}          # 路径 -> (内容, 权限覆盖或 None)
drops = set()
for a in sys.argv[3:]:
    k, v = a.split("=", 1)
    mode = None
    if k.startswith("+"):
        # +<八进制权限>:<路径>   —— ⚠ 必须补上文件类型位 0100000（S_IFREG）：
        # 只写 0755 时内核 initramfs 解包的 S_ISREG() 为假 ⇒ 该条目被**静默跳过**，
        # guest 里根本看不到文件（2026-10-01 实测：adbd 注入了却报"缺 /system/bin/adbd"）。
        head, _, path = k[1:].partition(":")
        if not path or not head:
            raise SystemExit("新增条目格式应为 +0755:<镜像内路径>=<本地文件>，收到: " + k)
        mode = int(head, 8) | 0o100000
        k = path
    key = norm_path(k)
    if v == "-":
        drops.add(key)
    else:
        repl[key] = (open(v, "rb").read(), mode)

data = gzip.open(src, "rb").read()
print("解压后", len(data), "字节", flush=True)

# ── 预扫描：记录镜像里已有哪些条目（决定 repl 里的 key 是"替换"还是"新增"）──
existing = set()
_off = 0
while _off + HDR <= len(data):
    if data[_off:_off + 6] not in (b"070701", b"070702"):
        break
    _f = lambda i: int(data[_off + i:_off + i + 8], 16)
    _size, _ns = _f(OFF_SIZE), _f(OFF_NAMESIZE)
    _name = data[_off + HDR:_off + HDR + _ns - 1].decode("utf-8", "replace")
    existing.add(norm_path(_name))
    _body = _off + HDR + _ns
    _body += (4 - (_body % 4)) % 4
    _off = _body + _size
    _off += (4 - (_off % 4)) % 4
adds = {k: v for k, v in repl.items() if k not in existing}
print("预扫描：镜像 %d 条目；待新增 %s" % (len(existing), sorted(adds) or "无"), flush=True)


def header(ino, mode, filesize, namesize):
    return (b"070701" + b"".join(b"%08X" % v for v in (
        ino, mode, 0, 0, 1, 0, filesize, 0, 0, 0, 0, namesize, 0)))


def emit_entry(out, name, body, mode, ino):
    nb = name.encode("utf-8")
    out.write(header(ino, mode, len(body), len(nb) + 1))
    out.write(nb + b"\0")
    out.write(b"\0" * ((4 - ((HDR + len(nb) + 1) % 4)) % 4))
    out.write(body)
    out.write(b"\0" * ((4 - (len(body) % 4)) % 4))


out = io.BytesIO()
off = 0
hits = {}
n = 0
ino = 300000
while off + HDR <= len(data):
    if data[off:off + 6] not in (b"070701", b"070702"):
        print("非 cpio 头，停在", off)
        break
    f = lambda i: int(data[off + i:off + i + 8], 16)
    fields = [f(i) for i in (OFF_INO, OFF_MODE, OFF_UID, OFF_GID, OFF_NLINK, OFF_MTIME)]
    filesize, devmaj, devmin, rdevmaj, rdevmin, namesize, check = (
        f(OFF_SIZE), f(OFF_DEVMAJ), f(OFF_DEVMIN), f(OFF_RDEVMAJ), f(OFF_RDEVMIN),
        f(OFF_NAMESIZE), f(OFF_CHECK))
    name = data[off + HDR:off + HDR + namesize - 1].decode("utf-8", "replace")
    body_off = off + HDR + namesize
    body_off += (4 - (body_off % 4)) % 4
    body = data[body_off:body_off + filesize]
    orig_size = filesize
    key = norm_path(name)
    # 新增条目必须在 TRAILER!!! **之前**写出（之后会被内核忽略）
    if key == "TRAILER!!!" and adds:
        for k, (b, m) in sorted(adds.items()):
            emit_entry(out, k, b, m if m is not None else 0o100644, ino)
            ino += 1
            hits[k] = len(b)
            print("  + 新增 %s（%d 字节，mode %o）" % (k, len(b), (m if m is not None else 0o100644) & 0o7777), flush=True)
        adds = {}
    if key in drops:
        drops.discard(key)
        hits[key] = 0
        off = body_off + orig_size
        off += (4 - (off % 4)) % 4
        n += 1
        continue        # 整条（含头）不写出 = 从镜像里删除
    mode = fields[1]
    if key in repl:
        body, mode_override = repl[key]
        filesize = len(body)          # 只影响输出；推进偏移必须用原长度
        if mode_override is not None:
            mode = mode_override
        hits[key] = filesize
    out.write(header(fields[0], mode, filesize, namesize))
    out.write(name.encode("utf-8") + b"\0")
    out.write(b"\0" * ((4 - ((HDR + namesize) % 4)) % 4))
    out.write(body)
    out.write(b"\0" * ((4 - (filesize % 4)) % 4))
    off = body_off + orig_size
    off += (4 - (off % 4)) % 4
    n += 1

if off < len(data):
    # 后面还有别的段（多段归档）：原样带上，别丢数据
    out.write(data[off:])
    print("尾部余量 %d 字节原样追加" % (len(data) - off), flush=True)

print("重写 %d 条目，命中: %s" % (n, hits), flush=True)

missing = [k for k in repl if k not in hits]
if missing:
    print("!! 这些 key 在镜像里既没匹配到、也没新增成功（拼错？）: %s" % missing, flush=True)

raw = out.getvalue()
d = os.path.dirname(dst)
if d:
    os.makedirs(d, exist_ok=True)
with gzip.GzipFile(dst, "wb", compresslevel=6, mtime=0) as g:
    g.write(raw)
print("输出", dst, os.path.getsize(dst), "字节", flush=True)
sys.exit(1 if missing else 0)
