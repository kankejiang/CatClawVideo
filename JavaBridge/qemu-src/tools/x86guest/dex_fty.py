#!/usr/bin/env python3
"""fty.jar（纯 class）→ d8 → 单个 classes.dex（供 x86 guest 原生速度基准）。
用法：python dex_fty.py  在 JavaBridge/qemu-src/art 下执行。"""
import os, subprocess, sys, zipfile

ART = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "art"))
JAR = os.path.join(ART, "fty.jar")
CLS = os.path.join(ART, "fty_cls")
OUT = os.path.join(ART, "fty_dexout")
D8 = r"C:\Users\lvjin\AppData\Local\Android\Sdk\build-tools\36.0.0\lib\d8.jar"
AJ = r"C:\Users\lvjin\AppData\Local\Android\Sdk\platforms\android-35\android.jar"
for p in (JAR, D8, AJ):
    if not os.path.isfile(p):
        raise SystemExit("缺文件: " + p)

import shutil
shutil.rmtree(CLS, ignore_errors=True)
os.makedirs(CLS)
with zipfile.ZipFile(JAR) as z:
    z.extractall(CLS)

files = [os.path.abspath(os.path.join(r, f)).replace("\\", "/")
         for r, _, fs in os.walk(CLS) for f in fs if f.endswith(".class")]
lst = os.path.join(ART, "fty_cls.txt")
open(lst, "w").write("\n".join(files) + "\n")
print("class 数:", len(files))

shutil.rmtree(OUT, ignore_errors=True)
os.makedirs(OUT)
r = subprocess.run(["java", "-cp", D8, "com.android.tools.r8.D8", "--min-api", "28",
                    "--lib", AJ, "--release", "--output", OUT, "@" + lst],
                   capture_output=True)
if r.returncode:
    print(r.stderr.decode("utf-8", "replace")[-1500:])
    raise SystemExit("d8 失败")
out = os.path.join(OUT, "classes.dex")
dst = os.path.join(ART, "fty.dex")
shutil.move(out, dst)
print("产出:", dst, os.path.getsize(dst), "bytes")
