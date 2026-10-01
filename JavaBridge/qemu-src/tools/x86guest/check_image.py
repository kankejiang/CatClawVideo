#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""x86 mini guest 镜像自检：断言「部署的 initrd 里该有的东西都在、且是当前版本」。

为什么需要它（2026-09-30 实测事故）：打包管线曾在**产物侧**静默退化 ——
  · init 缺整段迅雷段（磁力点了没下文；台架 M1 才失败，那时已经晚了）
  · gb.dex / ui_stub.dex 是 **pristine 里的旧版**（§2 缺陷1 与 §7 网盘修复全丢）
  · 注入器的索引错位把迅雷版 init 文本写进了 ui_stub.dex 槽位
这三样都「打包成功、启动不报错」，只有跑起来才发现。所以每次重打镜像后必须过一遍本脚本。

用法：
    python check_image.py [initrd.gz]        # 缺省查部署位 QemuGuest/x86guest/art_initrd_x64.gz
退出码：0 = 全部通过；1 = 有 FAIL（此时**不要**部署）。
"""
import gzip
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# tools/x86guest/ → 仓库根
REPO = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
ART = os.path.join(REPO, "JavaBridge", "qemu-src", "art")
DEFAULT_IMG = os.path.join(REPO, "CatClawVideo.Maui", "QemuGuest", "x86guest", "art_initrd_x64.gz")

HDR = 110


def read_cpio_newc(data):
    """newc cpio 解析（与 build_bootimg_inject.py 同款，保持行为一致）。"""
    entries, pos = [], 0
    while pos < len(data) - 6:
        if data[pos:pos + 6] != b"070701":
            pos += 1
            continue
        hdr = data[pos:pos + HDR]
        f = lambda i: int(hdr[6 + i * 8:6 + (i + 1) * 8], 16)
        (ino, mode, uid, gid, nlink, mtime, filesize, _a, _b, _c, _d,
         namesize, _chk) = (f(0), f(1), f(2), f(3), f(4), f(5), f(6), f(7),
                            f(8), f(9), f(10), f(11), f(12))
        name = data[pos + HDR:pos + HDR + namesize - 1].decode("utf-8", "replace")
        dpos = (pos + HDR + namesize + 3) & ~3
        fdata = data[dpos:dpos + filesize]
        pos = (dpos + filesize + 3) & ~3
        if name == "TRAILER!!!":
            break
        entries.append((name, fdata))
    return entries


def main():
    img = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_IMG
    if not os.path.exists(img):
        print("!! 找不到镜像:", img)
        return 1

    print("镜像:", img, "%.1f MB" % (os.path.getsize(img) / 1048576))
    entries = read_cpio_newc(gzip.open(img, "rb").read())

    def norm(n):
        """条目名归一：不同 cpio 写出器会带 './' 或 '/' 前缀（实测两条管线各用一套），
        不归一会把好镜像误判成「init 不存在」。"""
        while n.startswith("./"):
            n = n[2:]
        return n.lstrip("/")

    by = {}
    for n, b in entries:
        by[norm(n)] = b      # 同名后者覆盖前者（与解包语义一致）
    print("条目数:", len(entries))

    def md5(b):
        return hashlib.md5(b).hexdigest()

    init = by.get("init", b"")
    src_gb = open(os.path.join(ART, "gb.dex"), "rb").read()
    src_ui = open(os.path.join(ART, "ui_stub.dex"), "rb").read()

    # 「ARM 引擎库路径只能挂在 harness 命令上」纪律：该 LD_LIBRARY_PATH 串必须恰好出现 1 次
    arm_path = b"LD_LIBRARY_PATH=/data/catclaw/art/lib:/thunder-arm/system/lib64"
    misplaced = [n for n, b in by.items()
                 if n != "init" and b"[thunder]" in b and len(b) < 20000]
    checks = [
        ("init 存在", len(init) > 0),
        ("init 含迅雷段（[thunder]）", b"[thunder]" in init),
        ("init 会拉起 harness（qemu-aarch64 转译）",
         b"qemu-aarch64-static -L /thunder-arm /harness" in init),
        ("init 含 bootimg 补丁 -Ximage 全列",
         b"-Ximage:/system/framework/x86_64/boot.art" in init),
        # 2026-10-01：排障反向后门改为 opt-in（构建时设 DBGPORT=<非0> 才插）。
        # 默认镜像里**不应**有它：它是 while 循环每 2s 重试 10.0.2.2:18777，
        # 没有监听时会把 guest 控制台刷满 "nc: can't connect to remote host"
        # （实测 795 次），淹没有效日志、且是个常开的后门。
        ("init 不含排障反向后门（默认 opt-in，18777 应缺席）", b"10.0.2.2:18777" not in init),
        ("ARM 库路径只在 harness 命令行上（恰好 1 处）", init.count(arm_path) == 1),
        ("harness 资产在位", "harness" in by),
        ("qemu-aarch64-static 资产在位", "qemu-aarch64-static" in by),
        ("gb.dex 与源码产物一致", md5(by.get("gb.dex", b"")) == md5(src_gb)),
        ("ui_stub.dex 与源码产物一致", md5(by.get("ui_stub.dex", b"")) == md5(src_ui)),
        ("无错位写入（迅雷版 init 文本没被写进别的条目）", not misplaced),
        ("条目数像完整镜像（>2000）", len(entries) > 2000),
    ]

    failed = 0
    for label, ok in checks:
        print(("  PASS  " if ok else "  FAIL  ") + label)
        if not ok:
            failed += 1
    if misplaced:
        print("        错位嫌疑条目:", misplaced[:5])
    print("  init=%dB  gb.dex=%dB  ui_stub.dex=%dB"
          % (len(init), len(by.get("gb.dex", b"")), len(by.get("ui_stub.dex", b""))))

    if failed:
        print("!! 自检未通过 %d 项 —— 不要部署这个镜像" % failed)
        return 1
    print("镜像自检全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
