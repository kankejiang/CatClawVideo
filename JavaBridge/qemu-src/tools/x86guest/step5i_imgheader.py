# -*- coding: utf-8 -*-
# §6.10 Step5i：读 boot.art 的 ImageHeader（magic/版本/base/size）与 boot.oat 头信息
import struct

p = '/root/x86guest/art-tree/system/framework/arm64/boot.art'
with open(p, 'rb') as f:
    head = f.read(64)
print('前 4 字节（magic，应为 b"art\\n"）:', head[:4])
print('版本字段:', head[4:8])
base, size = struct.unpack('<QQ', head[8:24])
print('image base = 0x%x' % base)
print('image size = 0x%x (%d MB)' % (size, size // 1048576))

# boot.oat 头：oat 文件头 magic "oat\n"？
op = '/root/x86guest/art-tree/system/framework/arm64/boot.oat'
with open(op, 'rb') as f:
    oh = f.read(64)
print()
print('boot.oat 前 4 字节:', oh[:4])
print('boot.oat 版本:', oh[4:8])

# 也看看 init 里 CATCLAW_BCP_LOCATIONS 第一项对应的 arm64/boot.art（同一个文件）
import os
print()
print('符号链接解析:', os.path.realpath('/root/x86guest/art-tree/system/framework/boot.art'))
print('文件大小:', os.path.getsize(os.path.realpath('/root/x86guest/art-tree/system/framework/boot.art')))
