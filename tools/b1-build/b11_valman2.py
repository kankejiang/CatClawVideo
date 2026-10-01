#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验 android-stack.tar.gz 里的 manifest：同名 <hal> 是否只剩一个块；configstore 是否含 1.0+1.1。"""
import tarfile, io, xml.etree.ElementTree as ET
from collections import Counter

T = "/root/b1_blobs/android-stack.tar.gz"
tf = tarfile.open(T)
body = tf.extractfile("vendor/manifest.xml").read().decode("utf-8")
root = ET.fromstring(body)
hals = root.findall("hal")
c = Counter((h.findtext("name") or "") for h in hals)
dups = {k: v for k, v in c.items() if v > 1}
print("  hal 块数:", len(hals))
print("  同名重复:", dups if dups else "无")
for h in hals:
    n = h.findtext("name") or ""
    if "configstore" in n or "mapper" in n or "waydroid.task" in n:
        vs = [v.text for v in h.findall("version")]
        ifs = [(i.findtext("name"), i.findtext("instance")) for i in h.findall("interface")]
        print("  - %s  versions=%s  interfaces=%s  transport=%s" % (n, vs, ifs, h.findtext("transport")))
print("  文件大小:", len(body))
