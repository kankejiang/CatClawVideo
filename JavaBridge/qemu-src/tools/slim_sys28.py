#!/usr/bin/env python3
"""sys28 依赖闭包裁剪（libpdfium 事故后的修正版，2026-09-27）。

黑名单法已证伪：libandroid.so 静态链接 libpdfium.so，且 nativeloader 启动时
预加载 /system/etc/public.libraries.txt 的全部公共库——缺任何一个直接 abort
（实测 Kernel panic）。本脚本改用「引用闭包」：

  种子 = etc/public.libraries.txt 全列表
       + artlaunch（guest 的 ART 宿主进程，仓库 qemu-src/art/artlaunch）
       + bin/app_process64
       + lib64/libart.so、lib64/libandroid_runtime.so
       + tvbox.apk 里 lib/arm64-v8a/*.so（桥 System.load 的解析侧库）
       + 白名单（Java 层 System.loadLibrary 拼接目标，静态扫描不到）
  迭代 = 对每个保留 so 提取其 ASCII 中的 lib*.so 引用（.dynstr 与 dlopen
         字符串都是明文，超集偏安全），命中 lib64 实存文件则并入；
  不动点后，lib64 中不在闭包的文件剔除。framework/bin/etc/usr/xbin 原样保留
  （boot classpath 是成套件，framework 端本轮不动）。

用法：
    python slim_sys28.py --sys28 D:/Code/.shot/sys28 --tvbox <TVBox.apk> \
        --out D:/Code/.shot/sys28_slim
"""
import argparse, os, re, shutil, zipfile

LIB_RE = re.compile(r"lib[A-Za-z0-9_.+-]+?\.so")

# Java 层 System.loadLibrary("x") → libx.so 的拼接目标，静态扫描不到，强制保留。
WHITELIST = {
    "libopenjdk.so", "libopenjdkjvm.so", "libopenjdkd.so",
    "libjavacore.so", "libjavacrypto.so",
    "libhwui.so", "libandroid_servers.so",
    "libpdfium.so",  # libandroid 静态依赖（事故主角，双保险）
}


def refs_of(path):
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError:
        return set()
    return set(m.group(0) for m in LIB_RE.finditer(data.decode("latin-1")))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sys28", required=True, help="原 sys28 副本（只读，不动它）")
    ap.add_argument("--artlaunch", default=None, help="artlaunch 路径（缺省取 qemu-src/art/artlaunch）")
    ap.add_argument("--tvbox", required=True, help="TVBox apk（提供 arm64 解析库）")
    ap.add_argument("--out", required=True, help="裁剪输出目录（每次重建）")
    a = ap.parse_args()

    here = os.path.dirname(os.path.abspath(__file__))
    artlaunch = a.artlaunch or os.path.join(os.path.dirname(here), "art", "artlaunch")
    lib64 = os.path.join(a.sys28, "lib64")
    libs = set(n for n in os.listdir(lib64)
               if os.path.isfile(os.path.join(lib64, n)))

    # ── 种子 ──
    keep = set(n for n in WHITELIST if n in libs)
    pub = os.path.join(a.sys28, "etc", "public.libraries.txt")
    if os.path.isfile(pub):
        for ln in open(pub, encoding="utf-8", errors="replace"):
            ln = ln.strip()
            if ln in libs:
                keep.add(ln)
    seeds = [artlaunch, os.path.join(a.sys28, "bin", "app_process64"),
             os.path.join(lib64, "libart.so"), os.path.join(lib64, "libandroid_runtime.so")]
    if os.path.isfile(a.tvbox):
        with zipfile.ZipFile(a.tvbox) as z:
            for i in z.infolist():
                if i.filename.startswith("lib/arm64-v8a/"):
                    keep |= {n for n in LIB_RE.findall(
                        z.read(i.filename).decode("latin-1")) if n in libs}
    for s in seeds:
        if os.path.isfile(s):
            keep |= {n for n in refs_of(s) if n in libs}

    # ── 迭代至不动点 ──
    rounds = 0
    changed = True
    while changed:
        changed = False
        rounds += 1
        for name in list(keep):
            p = os.path.join(lib64, name)
            if not os.path.isfile(p):
                continue
            for ref in refs_of(p):
                if ref in libs and ref not in keep:
                    keep.add(ref)
                    changed = True

    # ── 生成 slim 副本 ──
    dropped = sorted(n for n in libs if n not in keep)
    shutil.rmtree(a.out, ignore_errors=True)
    shutil.copytree(a.sys28, a.out)
    for name in dropped:
        os.remove(os.path.join(a.out, "lib64", name))

    def mb(root):
        t = 0
        for r, _, fs in os.walk(root):
            for f in fs:
                try:
                    t += os.path.getsize(os.path.join(r, f))
                except OSError:
                    pass
        return t / 1048576

    print("slim_sys28(闭包): %d 轮收敛" % rounds)
    print("  lib64 保留 %d / 原 %d，剔除 %d 个" % (len(keep & libs), len(libs), len(dropped)))
    print("  白名单兜底命中: %d 个" % len(WHITELIST & libs))
    print("  原副本 %.1fMB → 裁剪后 %.1fMB" % (mb(a.sys28), mb(a.out)))
    print("  下一步: mk_art_initrd.py --sys28 %s --tvbox <apk> --skip-gb --out <ThunderRuntime/art_initrd.gz>" % a.out)


if __name__ == "__main__":
    main()
