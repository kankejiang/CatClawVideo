#!/usr/bin/env python3
"""块设备直读检查（108 上跑）：读 store-art.img 偏移 0，验证是否为 MP4 头。"""
import os

p = "/root/x86guest/store-art.img"
size = os.path.getsize(p)
with open(p, "rb") as f:
    b = f.read(64)
hexs = b[:16].hex()
ok = b[4:12] in (b"ftypisom", b"ftypiso6", b"ftypmp42", b"ftypisom")
print("镜像容量:", size // (1024 ** 3), "GB")
print("前16字节 hex:", hexs)
print("前4字节:", b[:4])
print("是 MP4 头(ftyp*):", ok)
print("已分配(du):", end=" ")
