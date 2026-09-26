#!/usr/bin/env python3
"""造 ART guest 的 initrd（<c>art_initrd.gz</c>）—— QEMU 里那套「真 Android ART + 桥 + TVBox」。

产物放进 <c>CatClawVideo.Maui/ThunderRuntime/</c>，宿主 <c>QemuArtGuest</c> 检测到它就启用 ART 链路
（判据：<c>qemu-system-aarch64.exe</c> + <c>pkg_kernel</c> + <c>art_initrd.gz</c> 三件齐全）。

两条硬规矩（都是 2026-09-25 实测踩出来的，别再撞）：
1. <b>桥的 dex 必须去掉「TVBox 与壳 jar 自己定义的类</b>」：guest 的 classpath 是
   <c>DexClassLoader</c> 的 parent，parent-first 下我们手写的替身会顶掉真 native
   （症状：桥里 <c>DexNative 方法 11 个，native 0 个</c>）。规则见 <c>gb_excluded()</c>。
2. <b>端口不能烧进 initrd</b>：/init 从 kernel cmdline 的 <c>guardport=</c> 取
   （宿主 QemuHostRuntime 已经带这条），否则两个 VM 并存必撞号。

用法（在 D:\\Code 下）：
    python CatClawVideo/JavaBridge/qemu-src/tools/mk_art_initrd.py ^
        --sys28 .shot/sys28 --links .shot/sys28_links.tsv ^
        --tvbox D:/Code/sourceCode-ccc25f6/app/build/outputs/apk/java64/debug/TVBox_debug-java64.apk
可选：--out 目标文件；--skip-gb 复用现成的 art/gb.dex。
"""
import argparse, gzip, io, os, shutil, subprocess, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))          # …/JavaBridge/qemu-src/tools
ART = os.path.normpath(os.path.join(HERE, "..", "art"))    # …/JavaBridge/qemu-src/art
BRIDGE = os.path.normpath(os.path.join(HERE, "..", ".."))  # …/JavaBridge
sys.path.insert(0, HERE)
import repack_initrd as RC   # 同目录：cpio 读写

BASE_INITRD_DEFAULT = os.path.normpath(
    os.path.join(BRIDGE, "..", "CatClawVideo.Maui", "ThunderRuntime", "pkg_initrd.gz"))
SDK_DEFAULT = os.path.expanduser(r"~\AppData\Local\Android\Sdk")


def sdk_tool(sdk, *rel):
    return os.path.join(sdk, *rel)


def dex_classes(dexdump, dex_path):
    """一个 dex 文件里**定义**的类描述符（dexdump -f 的 Class descriptor 行）。"""
    out = subprocess.run([dexdump, "-f", dex_path], capture_output=True).stdout
    out = out.decode("utf-8", "replace")
    import re
    return {m.group(1) for m in re.finditer(r"Class descriptor\s+: 'L(\S+?);'", out)}


def apk_classes(dexdump, apk, tmpdir):
    names = set()
    os.makedirs(tmpdir, exist_ok=True)
    with zipfile.ZipFile(apk) as z:
        for n in z.namelist():
            if not n.endswith(".dex"):
                continue
            p = os.path.join(tmpdir, "_t.dex")
            io.open(p, "wb").write(z.read(n))
            names |= dex_classes(dexdump, p)
    return names


def d8_to_dex(files, out_dex, java, d8, android_jar, workdir, tag):
    lst = os.path.join(workdir, tag + ".lst")
    io.open(lst, "w", encoding="utf-8").write("\n".join(files) + "\n")
    o = os.path.join(workdir, tag + "out")
    shutil.rmtree(o, ignore_errors=True)
    os.makedirs(o)
    r = subprocess.run([java, "-cp", d8, "com.android.tools.r8.D8", "--min-api", "28",
                        "--lib", android_jar, "--release", "--output", o, "@" + lst],
                       capture_output=True)
    if r.returncode:
        print(r.stderr.decode("utf-8", "replace")[-1500:])
        raise SystemExit("d8 失败")
    shutil.move(os.path.join(o, "classes.dex"), out_dex)


def build_gb_dex(bridge_jar, exclude, out_dex, java, d8, android_jar, workdir):
    """bridge.jar 的类 → 去掉 exclude → d8 成单个 dex。"""
    cls = os.path.join(workdir, "gbcls")
    shutil.rmtree(cls, ignore_errors=True)
    os.makedirs(cls)
    kept, dropped = 0, []
    with zipfile.ZipFile(bridge_jar) as z:
        for n in z.namelist():
            if not n.endswith(".class"):
                continue
            if n[:-6] in exclude:
                dropped.append(n[:-6])
                continue
            p = os.path.join(cls, n.replace("/", os.sep))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            io.open(p, "wb").write(z.read(n))
            kept += 1
    files = [os.path.abspath(os.path.join(r, f)).replace("\\", "/")
             for r, _, fs in os.walk(cls) for f in fs if f.endswith(".class")]
    d8_to_dex(files, out_dex, java, d8, android_jar, workdir, "gb")
    print("gb.dex: 留 %d 个类，交给 TVBox/壳 jar %d 个 → %s（%dB）"
          % (kept, len(dropped), out_dex, os.path.getsize(out_dex)))
    if dropped:
        print("   交出的类:", sorted(dropped)[:8], "…" if len(dropped) > 8 else "")


# 试过的死路（别再试第二遍，2026-09-26 实测）：把桥的 android UI 桩单独 dex 化、zipalign 后
# 放到 -Xbootclasspath 最前面，想抢回 android.app.AlertDialog 的类名 —— ART 对 boot classpath
# 里的重复类不是"先到先得"，`android.app.Dialog` 仍解析到 framework.jar（探针 op 里 fillSpec=无）。

def build_initrd(a):
    ents = [e for e in RC.read_cpio(gzip.decompress(io.open(a.base, "rb").read()))
            if e[0] not in ("init", "TRAILER!!!", ".") and not e[0].startswith("system/")]
    ents = [(".", 0o40755, 0, 0, 1, 0, 0, 0, 0, 0, b"")] + ents
    seen = set()

    def dirs_of(name):
        parts = name.split("/")
        for i in range(1, len(parts)):
            d = "/".join(parts[:i])
            if d and d not in seen:
                seen.add(d)
                ents.append((d, 0o40755, 0, 0, 1, 0, 0, 0, 0, 0, b""))

    # /system 里的绝对符号链接（framework/arm64/*.vdex → /system/framework/*.vdex）
    # 必须作为 cpio 符号条目写：rdump 到 Windows 上会变成打不开的真链接，
    # 少了它们 patchoat 报 "boot.vdex does not exist"。
    if os.path.isfile(a.links):
        for ln in io.open(a.links, encoding="utf-8", newline=""):
            ln = ln.strip()
            if not ln or "\t" not in ln:
                continue
            rel, tgt = ln.split("\t", 1)
            rel = "system/" + rel.lstrip("./").replace("\\", "/")
            dirs_of(rel)
            ents.append((rel, 0o120777, 0, 0, 1, 0, 0, 0, 0, 0, tgt.encode()))
    skipped = 0
    for root, ds, fs in os.walk(a.sys28):
        for f in fs:
            fp = os.path.join(root, f)
            rel = "system/" + os.path.relpath(fp, a.sys28).replace("\\", "/")
            try:
                data = io.open(fp, "rb").read()
            except OSError:
                skipped += 1
                continue
            dirs_of(rel)
            ents.append((rel, 0o100755, 0, 0, 1, 0, 0, 0, 0, 0, data))

    def add(name, data, mode=0o100644):
        dirs_of(name)
        ents.append((name, mode, 0, 0, 1, 0, 0, 0, 0, 0, data))

    def put(name, path, mode=0o100644):
        add(name, io.open(path, "rb").read(), mode)

    for f, mode in (("artlaunch", 0o100755), ("proppreload.so", 0o100644), ("fakelogd", 0o100755)):
        put(f, os.path.join(ART, f), mode)
    put("gb.dex", a.gb)
    put("tvbox.apk", a.tvbox)
    # TVBox 解析侧的 arm64 库：桥的 librarySearchPath 就是 /data/catclaw/art/lib（见 bridge.Art）
    with zipfile.ZipFile(a.tvbox) as z:
        for i in z.infolist():
            if i.filename.startswith("lib/arm64-v8a/"):
                add("data/catclaw/art/lib/" + i.filename.split("/")[-1], z.read(i.filename))
    init = io.open(os.path.join(ART, "init.tmpl.sh"), encoding="utf-8").read().replace("\r\n", "\n")
    add("init", init.encode(), 0o100755)

    raw = RC.write_cpio(ents)
    io.open(a.out, "wb").write(gzip.compress(raw, 6, mtime=0))   # mtime=0：产物可复现
    print("art_initrd: cpio %.1fMB → gz %.1fMB（%s），/system 跳过 %d 个打不开的文件"
          % (len(raw) / 1048576, os.path.getsize(a.out) / 1048576, a.out, skipped))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sys28", required=True, help="从 Android 9 镜像 debugfs rdump 出来的 /system 子集")
    ap.add_argument("--links", required=True, help="符号链接表（rel\\ttarget 每行一条）")
    ap.add_argument("--tvbox", required=True, help="TVBox APK（提供真 crawler 代码 + 解析侧 arm64 库）")
    ap.add_argument("--base", default=BASE_INITRD_DEFAULT, help="基座 initrd（提供 busybox/内核模块）")
    ap.add_argument("--out", default=os.path.join(ART, "art_initrd.gz"))
    ap.add_argument("--gb", default=os.path.join(ART, "gb.dex"))
    ap.add_argument("--sdk", default=SDK_DEFAULT)
    ap.add_argument("--bridge-jar", default=os.path.join(BRIDGE, "bridge.jar"))
    ap.add_argument("--skip-gb", action="store_true", help="复用现成 gb.dex（只重打 initrd）")
    a = ap.parse_args()

    if not a.skip_gb:
        dexdump = sdk_tool(a.sdk, "build-tools", "36.0.0", "dexdump.exe")
        d8 = sdk_tool(a.sdk, "build-tools", "36.0.0", "lib", "d8.jar")
        aj = sdk_tool(a.sdk, "platforms", "android-35", "android.jar")
        for p in (dexdump, d8, aj, a.bridge_jar):
            if not os.path.isfile(p):
                raise SystemExit("缺文件：" + p)
        exclude = apk_classes(dexdump, a.tvbox, os.path.join(ART, "_tmp"))
        shutil.rmtree(os.path.join(ART, "_tmp"), ignore_errors=True)
        # 壳 jar 自己定义 com/github/catvod/spider/**（Init/DexNative/XxxGuard），
        # 而第三方 jar 是开放集合 —— 这个包整片让给 jar，永不让桥替身参与。
        #
        # 但 **crawler/SpiderApi 与 crawler/SpiderDebug 要留桥的替身**（2026-09-25 实测定死）：
        # TVBox 那两个类没有我们需要的能力（SpiderApi.getAddress 走它自己的 ControlManager，
        # 也没有 setHostProxyPort），而"云盘配置"的 URL 必须指回宿主的 proxy 端口；
        # guest 里 SpiderApi 只由桥 new 出来给 spider，所以两套不会同时出现。
        # 反过来 crawler/Spider 必须让给 TVBox —— 壳的 BaseSpiderGuard 继承它，是**类型身份**问题。
        exclude = {c for c in exclude if c not in KEEP_FROM_BRIDGE}
        exclude |= JAR_OWNED
        build_gb_dex(a.bridge_jar, exclude, a.gb, "java", d8, aj, ART)
    build_initrd(a)


# 属于「jar 自己」的类名：桥里出现任何一个都会在 guest 里顶掉真实现
KEEP_FROM_BRIDGE = {
    "com/github/catvod/crawler/SpiderApi",
    "com/github/catvod/crawler/SpiderDebug",
}

JAR_OWNED = {
    "com/github/catvod/spider/DexNative",
    "com/github/catvod/spider/Init",
    "com/github/catvod/spider/Proxy",
    "com/github/catvod/spider/BaseSpiderGuard",
}

main()
