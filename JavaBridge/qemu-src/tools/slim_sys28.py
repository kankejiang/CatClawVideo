#!/usr/bin/env python3
"""把 <c>sys28</c>（Android 9 /system dump 副本）拷成裁剪版 <c>sys28_slim</c>。

只剔除与「跑爬虫桥」无关的库（原副本不动，可反复重跑）；随后用
<c>mk_art_initrd.py --sys28 &lt;slim&gt;</c> 重打 art_initrd.gz。

第一轮黑名单（2026-09-26，均为零依赖风险的独立功能块）：
- libLLVM_android / libRS* / libbcinfo / libblas —— Renderscript 编译与运行时（桥不跑 RenderScript）
- libartd* / libclang_rt.* —— ART 的 debug 变体与 sanitizer 运行时（release libart 不依赖它们）
- libpac —— WebView 的代理自动配置引擎
- libpdfium / libbluetooth / libjni_latinime —— PDF / 蓝牙 / 输入法词典

第二轮候选（需 guest 启动实测后再加）：media/camera/surfaceflinger 系服务库、
libprotobuf-cpp-full、libchrome、telephony-common 等 boot 分件。

用法：
    python slim_sys28.py --sys28 D:/Code/.shot/sys28 --out D:/Code/.shot/sys28_slim
"""
import argparse, glob, os, shutil, sys

BLACKLIST = [
    "lib64/libLLVM_android.so",
    "lib64/libRSCpuRef.so",
    "lib64/libbcinfo.so",
    "lib64/libblas.so",
    "lib64/libRS*.so",
    "lib64/libpac.so",
    "lib64/libartd.so",
    "lib64/libartd-compiler.so",
    "lib64/libartd-*.so",
    "lib64/libclang_rt.*.so",
    "lib64/libpdfium.so",
    "lib64/libbluetooth.so",
    "lib64/libjni_latinime.so",
]


def dir_size(root):
    total = 0
    for r, _, fs in os.walk(root):
        for f in fs:
            try:
                total += os.path.getsize(os.path.join(r, f))
            except OSError:
                pass
    return total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sys28", required=True, help="原 sys28 副本（不动它）")
    ap.add_argument("--out", required=True, help="裁剪输出目录（每次重建）")
    a = ap.parse_args()

    removed, saved = 0, 0
    shutil.rmtree(a.out, ignore_errors=True)
    shutil.copytree(a.sys28, a.out)

    doomed = set()
    for pat in BLACKLIST:
        doomed.update(glob.glob(os.path.join(a.out, pat.replace("/", os.sep))))
    for p in sorted(doomed):
        try:
            saved += os.path.getsize(p)
            os.remove(p)
            removed += 1
        except OSError as e:
            print("  跳过（占用/缺失）:", p, e)

    print("slim_sys28: 剔除 %d 个文件，省 %.1fMB（未压缩）" % (removed, saved / 1048576))
    print("  原副本 %.1fMB → 裁剪后 %.1fMB" % (dir_size(a.sys28) / 1048576, dir_size(a.out) / 1048576))
    print("  下一步：mk_art_initrd.py --sys28 %s --skip-gb --out <ThunderRuntime/art_initrd.gz>" % a.out)


if __name__ == "__main__":
    main()
