#!/usr/bin/env python3
"""Windows x86 initramfs 组装 v2：symlink 链式解析到实体 + cpio.gz"""
import os, gzip, shutil, fnmatch
EXT = r"D:\Code\.shot\waydroid\extracted"
W = r"D:\Code\.shot\x86guest"
ART = r"D:\Code\CatClawVideo\JavaBridge\qemu-src\art"
R = os.path.join(W, "rootfs_x86")
MOD = os.path.join(W, "linux-data", "lib", "modules", "6.1.0-50-amd64")
APX = ["com.android.art", "com.android.i18n", "com.android.conscrypt", "com.android.runtime", "com.android.os.statsd", "com.android.tzdata"]
JARS = ["core-oj.jar", "core-libart.jar", "okhttp.jar", "bouncycastle.jar", "apache-xml.jar", "conscrypt.jar"]
MODULES = ["virtio_ring", "virtio", "virtio_pci_modern_dev", "virtio_pci_legacy_dev", "virtio_pci", "virtio_blk", "net_failover", "failover", "virtio_net", "binder_linux"]
LD_CONFIG = open(os.path.join(W, "ld_config_x86.txt"), encoding="utf-8").read()
def is_real_file(p): return os.path.isfile(p) and not os.path.islink(p)
def resolve_link_chain(path, depth=6):
    for _ in range(depth):
        if not os.path.islink(path): return path
        try: tgt = os.readlink(path).replace("\\", "/")
        except OSError: return path
        cand = None
        if os.path.exists(tgt) and not os.path.islink(tgt): cand = tgt
        elif tgt.startswith("/"):
            c2 = os.path.normpath(os.path.join(EXT, tgt.lstrip("/")))
            if os.path.exists(c2): cand = c2
            c3 = os.path.normpath(os.path.join(R, tgt.lstrip("/")))
            if cand is None and os.path.exists(c3): cand = c3
        if cand is None: return path
        path = cand
    return path

def build_rootfs():
    shutil.rmtree(R, ignore_errors=True)
    os.makedirs(os.path.join(R, "system"), exist_ok=True)
    os.makedirs(os.path.join(R, "apex"), exist_ok=True)
    for d in ("framework", "lib64", "bin", "etc"):
        shutil.copytree(os.path.join(EXT, "system", d), os.path.join(R, "system", d), symlinks=True, ignore_dangling_symlinks=True)
    for m in APX:
        shutil.copytree(os.path.join(EXT, "system", "apex", m), os.path.join(R, "apex", m), symlinks=True, ignore_dangling_symlinks=True)
    for pat in ("boot*.art", "boot*.oat", "boot*.vdex", "boot*.bprof"):
        for f in fnmatch.filter(os.listdir(os.path.join(R, "system", "framework")), pat):
            os.remove(os.path.join(R, "system", "framework", f))
    for d in ("x86_64", "arm64"):
        shutil.rmtree(os.path.join(R, "system", "framework", d), ignore_errors=True)
    p = os.path.join(R, "apex", "com.android.art", "bin", "dex2oat64")
    if os.path.islink(p) or os.path.exists(p): os.remove(p)
    os.makedirs(os.path.join(R, "system", "javalib"), exist_ok=True)
    for j in JARS:
        s = os.path.join(EXT, "system", "system", "apex", "com.android.art" if j != "conscrypt.jar" else "com.android.conscrypt", "javalib", j)
        s = resolve_link_chain(s)
        if is_real_file(s): shutil.copy2(s, os.path.join(R, "system", "javalib", j))
        else: print("  跳过缺失:", j)
    b = os.path.join(EXT, "system", "bin", "bootstrap", "linker64")
    if is_real_file(b): shutil.copy2(b, os.path.join(R, "system", "bin", "linker64"))
def add_components():
    for d in ("bin", "modules", "dev", "proc", "sys", "tmp", "data/catclaw", "data/local/tmp", "data/dalvik-cache/x86_64", "data/misc", "data/system", "product", "system_ext", "odm", "vendor", "system/system_ext", "linkerconfig", "system/javalib"):
        os.makedirs(os.path.join(R, d), exist_ok=True)
    for src, dst in [
        (os.path.join(W, "bb-data", "bin", "busybox"), "bin/busybox"),
        (os.path.join(ART, "artlaunch.x64"), "artlaunch"),
        (os.path.join(ART, "artlaunch.x64"), "system/bin/artlaunch"),
        (os.path.join(ART, "proppreload_x64.so"), "proppreload.so"),
        # 2026-10-03 Go 代理链：qemu-user 解释器（ndk runner 跑 Go 静默 139）+ binfmt 自测件
        (os.path.join(ART, "qemu-aarch64-static"), "bin/qemu-aarch64-static"),
        (os.path.join(ART, "binfmt_test_arm64"), "binfmt_test_arm64"),
        # 2026-10-03 GoProxy 流隧道：并发 TCP 转发（0.0.0.0:25266 → 127.0.0.1:5266），
        # pvideo 只绑回环、slirp 直连被 RST；init 起 tcpfwd，宿主 hostfwd GoProxyTunnel → 25266
        (os.path.join(ART, "tcpfwd"), "bin/tcpfwd"),
        (os.path.join(ART, "fakelogd.x64"), "fakelogd"),
        (os.path.join(ART, "gb.dex"), "gb.dex"),
        (r"D:\Code\sourceCode-ccc25f6\app\build\outputs\apk\java64\debug\TVBox_debug-java64.apk", "tvbox.apk"),
    ]:
        if os.path.exists(src): shutil.copy2(src, os.path.join(R, dst))
        else: print("  缺组件:", src)
    for m in MODULES:
        hit = None
        for r_, _, fs in os.walk(MOD):
            if m + ".ko" in fs: hit = os.path.join(r_, m + ".ko"); break
        if hit: shutil.copy2(hit, os.path.join(R, "modules", m + ".ko"))
        else: print("  模块缺失:", m)
    open(os.path.join(R, "linkerconfig", "ld.config.txt"), "w", newline="\n").write(LD_CONFIG)
    open(os.path.join(R, "apex", "apex-info-list.xml"), "w", newline="\n").write("<?xml version=\"1.0\" encoding=\"utf-8\"?><apex-info-list>%s</apex-info-list>" % "".join("<apex-info moduleName=\"%s\" versionCode=\"1\" versionName=\"1\" isFactory=\"true\" isActive=\"true\" lastUpdateSeconds=\"0\" originalPath=\"/apex/%s\"/>" % (m, m) for m in APX))
def fix_dangling():
    fixed, dangling = 0, []
    for dirpath, dirnames, filenames in os.walk(R):
        for f in filenames:
            full = os.path.join(dirpath, f)
            if not os.path.islink(full) or os.path.exists(full): continue
            real = resolve_link_chain(full)
            if is_real_file(real):
                os.remove(full); shutil.copy2(real, full); fixed += 1
            else: dangling.append((os.path.relpath(full, R), os.readlink(full)))
    print("  悬空链接修复 %d 个" % fixed)
    for name, tgt in dangling[:10]: print("  未解析:", name, "->", tgt)
def cpio_pack(root, outpath):
    out = bytearray(); state = {"ino": 100}
    def pad():
        while len(out) % 4: out.append(0)
    def add(name, mode, data=b""):
        state["ino"] += 1
        nameb = name.encode("utf-8") + b"\0"
        fields = [state["ino"], mode, 0, 0, 1, 0, len(data), 0, 0, 0, 0, len(nameb), 0]
        out.extend(b"070701" + "".join("%08X" % f for f in fields).encode("ascii"))
        out.extend(nameb); pad(); out.extend(data); pad()
    for dirpath, dirnames, filenames in os.walk(root):
        rel = os.path.relpath(dirpath, root).replace("\\", "/")
        if rel != ".": add(rel, 0o040755)
        for f in sorted(filenames):
            full = os.path.join(dirpath, f)
            name = os.path.relpath(full, root).replace("\\", "/")
            if os.path.islink(full): add(name, 0o120777, os.readlink(full).replace("\\", "/").encode("utf-8"))
            else: add(name, 0o100755, open(full, "rb").read())
    add("TRAILER!!!", 0)
    pad(); out.extend(b"\0" * 1024)
    with gzip.GzipFile(outpath, "wb", mtime=0) as g: g.write(bytes(out))
build_rootfs()
add_components()
fix_dangling()
cpio_pack(R, os.path.join(W, "art_initrd_x64.gz"))
total = 0
for r_, _, fs in os.walk(R):
    for f in fs:
        try: total += os.path.getsize(os.path.join(r_, f))
        except OSError: pass
print("rootfs 未压缩: %.0fMB  产物: %s (%.0fMB)" % (total/1048576, os.path.join(W, "art_initrd_x64.gz"), os.path.getsize(os.path.join(W, "art_initrd_x64.gz"))/1048576))
