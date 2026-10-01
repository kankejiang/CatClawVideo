#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""导出 libSurfaceFlingerProp.so 里所有 sysprop::* 的 mangled 名 + 参数类型，并按类型分类。
用途：生成"直接返回默认值"的拦截器（跳过 configstore）。
注意：C++ mangling 不编码**返回类型**；但这些是生成的配置读取函数，返回类型 = 参数类型
（标量情况）。std::string / optional 等非标量的先跳过（SRet ABI 复杂）。"""
import struct, subprocess, os, json, re

S = "/mnt/b11sys"
if not os.path.ismount(S):
    os.makedirs(S, exist_ok=True)
    subprocess.run(["mount", "-o", "ro,loop", "/var/lib/waydroid/images/system.img", S], check=False)
path = os.path.join(S, "system/lib64/libSurfaceFlingerProp.so")
data = open(path, "rb").read()
e_shoff, = struct.unpack_from("<Q", data, 0x28)
e_shentsize, e_shnum, e_shstrndx = struct.unpack_from("<HHH", data, 0x3A)
secs = []
for i in range(e_shnum):
    off = e_shoff + i * e_shentsize
    name, typ, flags, addr, offset, size, link, info, align, entsize = struct.unpack_from("<IIQQQQIIQQ", data, off)
    secs.append(dict(name=name, addr=addr, offset=offset, size=size, entsize=entsize))
shstr = secs[e_shstrndx]
def sn(i):
    st = shstr["offset"] + i
    return data[st:data.index(b"\0", st)].decode("utf-8", "replace")
dynsym = dynstr = None
for s in secs:
    n = sn(s["name"])
    if n == ".dynsym": dynsym = s
    elif n == ".dynstr": dynstr = s
strtab = data[dynstr["offset"]:dynstr["offset"] + dynstr["size"]]
cnt = dynsym["size"] // dynsym["entsize"]
syms = []
for i in range(cnt):
    off = dynsym["offset"] + i * dynsym["entsize"]
    st_name, st_info, st_other, st_shndx, st_value, st_size = struct.unpack_from("<IBBHQQ", data, off)
    e = strtab.index(b"\0", st_name)
    nm = strtab[st_name:e].decode("utf-8", "replace")
    if (st_info & 0xF) in (2, 10) and st_value and "7sysprop" in nm:
        syms.append(nm)
syms.sort()
dm = subprocess.run(["c++filt"] + syms, capture_output=True, text=True).stdout.splitlines()

def classify(d: str):
    m = re.search(r"sysprop::\w+\((.*)\)$", d)
    if not m:
        return "unknown"
    arg = m.group(1)
    if arg in ("bool",): return "bool"
    if arg in ("int",): return "int"
    if arg in ("long", "long long"): return "long"
    if arg.startswith("std::__1::basic_string"): return "string"
    if arg.startswith("std::__1::optional"): return "optional"
    if "vector" in arg: return "vector"
    return "other:" + arg[:40]

rows = []
for nm, d in zip(syms, dm):
    rows.append({"mangled": nm, "demangled": d, "kind": classify(d)})

from collections import Counter
print("  总数:", len(rows))
print("  类型分布:", dict(Counter(r["kind"] for r in rows)))
print()
for r in rows:
    if r["kind"] in ("bool", "int", "long"):
        print("SCALAR %s | %s" % (r["kind"], r["mangled"]))
print()
for r in rows:
    if r["kind"] not in ("bool", "int", "long"):
        print("SKIP(%s) %s" % (r["kind"], r["demangled"][:100]))
