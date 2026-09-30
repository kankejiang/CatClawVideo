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
    # ⚠ 只排除 android/graphics/drawable/**（2026-09-29）：Drawable 挂在真 framework
    # 方法签名上（Activity.getWindow().setBackgroundDrawable），桩 Drawable 与 boot 里的
    # 真 Drawable 同名不同类 → ART verifier 直接拒：register v4 has type Drawable but
    # expected Drawable（玩偶 WoGG→Pan→ProxyOrigin.getan 整条播放链路因此死在 VerifyError）。
    # ⚠ 其余 android/graphics 值类**必须留在桩命名空间里**（2026-09-30 实测回归）：扫码
    # 二维码那条路要靠桩 Bitmap（UiBridge 用它的 snapshotPixels 抓黑白矩阵上行宿主），
    # 而 guest 的 ART 没有注册 android.graphics 的 native（Paint.nInit /
    # ColorSpace$Rgb.nativeCreate / Bitmap.nativeCreate 全是 UnsatisfiedLinkError）——
    # 放给平台类 ⇒ jar 构二维码时当场抛错并静默吞掉 ⇒ action("quark_scan") 回空、
    # 宿主一整天收不到 ui-dialog，只剩「改用粘贴 Cookie」兜底 toast。
    # ⚠ 实验（2026-09-30，第二轮）：整体恢复 android/graphics 的桩族（含 drawable/**）。
    # 上一轮只留「除 drawable 外」的 graphics 桩，实测 action("quark_scan") 已过
    # z.P1 的 TextPaint 校验，但仍死在 UnsatisfiedLinkError: Paint.nInit ——
    # 说明还有**平台** graphics 对象在构造（GradientDrawable/BitmapDrawable 这类
    # 具体 Drawable 的构造函数内部会 new Paint(...)，而 guest 的 ART 没注册任何
    # android.graphics native）。drawable 只留平台类就绕不开这一点；只有当整个
    # graphics 族（Drawable + 具体子类 + Canvas/ColorFilter）都是桩时，jar 的
    # 图形世界才自洽（JRE 时代扫码能用的配置）。
    # 待验证的代价：2026-09-29 记录过「桩 Drawable 与平台签名交叉 ⇒
    # 玩偶 ProxyOrigin.getan 的 VerifyError」，需用荐片/玩偶 homeContent+playerContent 回归确认。
    skip = ()
    with zipfile.ZipFile(bridge_jar) as z:
        for n in z.namelist():
            if not (n.startswith(prefixes) and n.endswith(".class")):
                continue
            if n.startswith(skip):
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
