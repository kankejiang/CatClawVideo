#!/usr/bin/env python3
"""只重打 gb.dex（+ui_stub.dex），**不动** aarch64 现网 initrd。

何时用：改了 JavaBridge/src 的桥代码后，只需更新 guest 侧的 dex 时跑它——
比跑完整 mk_art_initrd.py 安全（后者会连带重写 art_initrd.gz 这个现网产品件）。
规则与 mk_art_initrd.py 的 build_gb_dex 完全一致（exclude = TVBox apk 的类
− KEEP_FROM_BRIDGE + JAR_OWNED），务必保持两处同步。
"""
import io, os, re, shutil, subprocess, zipfile

TOOLS = os.path.dirname(os.path.abspath(__file__))
ART = os.path.normpath(os.path.join(TOOLS, "..", "art"))
BRIDGE = os.path.normpath(os.path.join(TOOLS, "..", ".."))
SDK = os.path.expanduser(r"~\AppData\Local\Android\Sdk")
TVBOX = r"D:\Code\sourceCode-ccc25f6\app\build\outputs\apk\java64\debug\TVBox_debug-java64.apk"

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


def dex_classes(dexdump, dex_path):
    out = subprocess.run([dexdump, "-f", dex_path], capture_output=True).stdout
    out = out.decode("utf-8", "replace")
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
    cls = os.path.join(workdir, "gbcls")
    shutil.rmtree(cls, ignore_errors=True)
    os.makedirs(cls)
    kept = 0
    dropped = []
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
    print("gb.dex: 留 %d 个类，让给 TVBox/壳 %d 个 → %s（%dB）"
          % (kept, len(dropped), out_dex, os.path.getsize(out_dex)))


def build_ui_stub_dex(bridge_jar, out_dex, java, d8, android_jar, workdir):
    cls = os.path.join(workdir, "uistubcls")
    shutil.rmtree(cls, ignore_errors=True)
    os.makedirs(cls)
    kept = 0
    prefixes = ("android/", "com/github/catvod/crawler/")
    with zipfile.ZipFile(bridge_jar) as z:
        for n in z.namelist():
            if not (n.startswith(prefixes) and n.endswith(".class")):
                continue
            p = os.path.join(cls, n.replace("/", os.sep))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            io.open(p, "wb").write(z.read(n))
            kept += 1
    files = [os.path.abspath(os.path.join(r, f)).replace("\\", "/")
             for r, _, fs in os.walk(cls) for f in fs if f.endswith(".class")]
    d8_to_dex(files, out_dex, java, d8, android_jar, workdir, "uistub")
    print("ui_stub.dex: %d 个桩类 → %s（%dB）" % (kept, out_dex, os.path.getsize(out_dex)))


def main():
    dexdump = os.path.join(SDK, "build-tools", "36.0.0", "dexdump.exe")
    d8 = os.path.join(SDK, "build-tools", "36.0.0", "lib", "d8.jar")
    aj = os.path.join(SDK, "platforms", "android-35", "android.jar")
    bridge_jar = os.path.join(BRIDGE, "bridge.jar")
    for p in (dexdump, d8, aj, bridge_jar, TVBOX):
        if not os.path.isfile(p):
            raise SystemExit("缺文件：" + p)
    exclude = apk_classes(dexdump, TVBOX, os.path.join(ART, "_tmp"))
    shutil.rmtree(os.path.join(ART, "_tmp"), ignore_errors=True)
    exclude = {c for c in exclude if c not in KEEP_FROM_BRIDGE}
    exclude |= JAR_OWNED
    build_gb_dex(bridge_jar, exclude, os.path.join(ART, "gb.dex"), "java", d8, aj, ART)
    build_ui_stub_dex(bridge_jar, os.path.join(ART, "ui_stub.dex"), "java", d8, aj, ART)


if __name__ == "__main__":
    main()
