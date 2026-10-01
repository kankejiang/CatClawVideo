#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""B1.1: 打包 Android 服务栈（servicemanager / hwservicemanager / surfaceflinger + 图形 HAL）
及其**递归依赖闭包**，供 guest 解包即用。

为什么要这么干：Android 二进制用 bionic linker，依赖数十个 .so，手工挑必然漏。
搜索路径按 Android 约定：/system/lib64、/vendor/lib64、/vendor/lib64/hw。
"""
import os, re, shutil, subprocess, tarfile

SYS = "/mnt/b11sys"     # waydroid system.img
VEN = "/mnt/b11ven"     # waydroid vendor.img
STAGE = "/root/b1_blobs/astage"
OUT = "/root/b1_blobs/android-stack.tar.gz"

SEARCH = [f"{SYS}/system/lib64", f"{VEN}/lib64", f"{VEN}/lib64/hw", f"{SYS}/system/lib64/hw"]

# mesa 的软件 GL（llvmpipe）：这个 SurfaceFlinger 版本**没有 CPU 渲染后端**
# （strings 里搜不到 skiacpu），RenderEngine 必须有可用的 EGL ⇒ 必须带 GL。
# libgallium_dri.so 含 llvmpipe（纯软件，无需 GPU）；压缩后约 12~15MB。
MESA = [
    f"{VEN}/lib64/egl/libEGL_mesa.so",
    f"{VEN}/lib64/egl/libGLESv2_mesa.so",
    f"{VEN}/lib64/egl/libGLESv1_CM_mesa.so",
    f"{VEN}/lib64/libgallium_dri.so",
    f"{VEN}/lib64/libgbm_mesa.so",
    f"{VEN}/lib64/libgbm_mesa_wrapper.so",
    f"{VEN}/lib64/dri_gbm.so",
    f"{VEN}/lib64/libminigbm_gralloc_gbm_mesa.so",
]

SEEDS = [
    # ⚠ 这里**不要**再放 MESA：mesa 的软件 GL 由 pack_mesa_gl.py 单独打成 mesa-gl.tar.gz。
    # 放进来会通过递归闭包把 105MB 的 libLLVM22 拖进来（包从 ~10MB 吹到 53MB，实测）。
    # ── 图形 HAL 服务（mesa 的 Android EGL 平台初始化需要 mapper@4.0 服务；
    #    SurfaceFlinger 需要 composer）── 两个都是 vendor 下的二进制，见 vendor/etc/init/*.rc。
    f"{VEN}/bin/hw/android.hardware.graphics.allocator@4.0-service.minigbm_gbm_mesa",
    f"{VEN}/bin/hw/android.hardware.graphics.composer@2.1-service",
    f"{VEN}/bin/hw/android.hardware.graphics.allocator@2.0-service",
    f"{VEN}/bin/hw/android.hardware.configstore@1.1-service",
    # Waydroid 专有：SF/HWC 会等它（system 镜像里，不在 vendor）
    f"{SYS}/system/bin/hw/vendor.waydroid.task@1.0-service",
    f"{SYS}/system/bin/servicemanager",
    f"{SYS}/system/bin/hwservicemanager",
    f"{SYS}/system/bin/surfaceflinger",
    f"{VEN}/lib64/hw/hwcomposer.waydroid.so",
    f"{VEN}/lib64/hw/gralloc.gbm.so",
    f"{VEN}/lib64/hw/gralloc.minigbm_gbm_mesa.so",
    f"{VEN}/lib64/hw/android.hardware.graphics.mapper@4.0-impl.minigbm_gbm_mesa.so",
    f"{VEN}/lib64/hw/android.hardware.graphics.allocator@2.0-impl.so",
]

for p in (SYS, VEN):
    os.makedirs(p, exist_ok=True)     # 挂载点必须先存在
    if not os.path.ismount(p):
        subprocess.run(["mount", "-o", "ro,loop",
                        "/var/lib/waydroid/images/system.img" if p == SYS else "/root/x86guest/vendor-ex/vendor.img",
                        p], check=False)
    if not os.path.ismount(p):
        raise SystemExit(f"!! {p} 未挂载")

def needed(path):
    try:
        out = subprocess.run(["readelf", "-d", path], capture_output=True, text=True, timeout=20).stdout
    except Exception:
        return []
    # ⚠ readelf 的输出会随 locale 变（实测 108 上是中文"共享库：[libbase.so]"），
    # 所以只锚定 (NEEDED) 与方括号里的名字，别匹配英文标签。
    return re.findall(r"\(NEEDED\).*?\[([^\]]+)\]", out)

def resolve(name):
    for d in SEARCH:
        p = os.path.join(d, name)
        if os.path.exists(p):
            return p
    return None

seen, missing, queue = set(), set(), [s for s in SEEDS if os.path.exists(s)]
while queue:
    p = os.path.realpath(queue.pop())
    if p in seen:
        continue
    seen.add(p)
    for n in needed(p):
        r = resolve(n)
        if r and os.path.realpath(r) not in seen:
            queue.append(r)
        elif not r:
            missing.add(n)

print("  闭包:", len(seen), "个；未解析:", sorted(missing)[:8], "（共 %d）" % len(missing))

# 落盘：一律放 /system/lib64（默认命名空间搜得到）；HAL .so 另外复制一份到 /system/lib64/hw
# 与 /vendor/lib64/hw —— hw_get_module 只认 hw/ 子目录。
shutil.rmtree(STAGE, ignore_errors=True)
libdir = os.path.join(STAGE, "system/lib64")
bindir = os.path.join(STAGE, "system/bin")
hwdir_s = os.path.join(libdir, "hw")
hwdir_v = os.path.join(STAGE, "vendor/lib64/hw")
for d in (libdir, bindir, hwdir_s, hwdir_v):
    os.makedirs(d, exist_ok=True)

total = 0
for p in sorted(seen):
    if p.startswith(f"{SYS}/system/bin/hw/"):
        # system 侧的 HAL 服务（如 Waydroid 的 vendor.waydroid.task@1.0-service）
        # 必须落在 /system/bin/hw（rc 与我们的 init 都用这个路径）
        shw = os.path.join(STAGE, "system/bin/hw")
        os.makedirs(shw, exist_ok=True)
        dst = os.path.join(shw, os.path.basename(p))
    elif p.startswith(f"{SYS}/system/bin/"):
        dst = os.path.join(bindir, os.path.basename(p))
    elif p.startswith(f"{VEN}/bin/"):
        # vendor 的 HAL 服务二进制：放 /vendor/bin/hw（rc 里就是这个路径）
        vhw = os.path.join(STAGE, "vendor/bin/hw")
        os.makedirs(vhw, exist_ok=True)
        dst = os.path.join(vhw, os.path.basename(p))
    else:
        dst = os.path.join(libdir, os.path.basename(p))
    shutil.copy2(p, dst)
    total += os.path.getsize(dst)
    if "/hw/" in p:   # HAL：hw/ 双份
        shutil.copy2(p, os.path.join(hwdir_s, os.path.basename(p)))
        shutil.copy2(p, os.path.join(hwdir_v, os.path.basename(p)))

# SONAME 链接（bionic 也按 SONAME 找）
extra = 0
for d in SEARCH:
    if not os.path.isdir(d):
        continue
    for n in os.listdir(d):
        full = os.path.join(d, n)
        if os.path.islink(full) and os.path.realpath(full) in seen:
            dst = os.path.join(libdir, n)
            if not os.path.lexists(dst):
                os.symlink(os.path.basename(os.path.realpath(full)), dst)
                extra += 1

# ── VINTF manifest：HIDL 服务**必须**在 manifest 里才能注册（这是 SF 崩溃链的上游）──
# 实测根因链：/vendor/manifest.xml 缺失 ⇒ hwservicemanager 报
#   getTransport: Cannot find entry …IAllocator/default in either framework or device VINTF manifest.
#   Service … must be in VINTF manifest in order to register/get.
# ⇒ mapper@4.0 注册不上 ⇒ libui 的 GraphicBufferMapper 构造 LOG_ALWAYS_FATAL ⇒ SurfaceFlinger abort。
# hwservicemanager 读的是**字面路径** /vendor/manifest.xml 与 /system/manifest.xml
# （真机上这俩是 init 建的符号链接；我们不跑 init ⇒ 直接放这两个路径）。
# vendor 侧要**合并三份**：主 manifest（含 configstore 等）+ gbm_mesa 的 allocator/mapper 两个
# fragment（rc 里它们默认 disabled、靠 bind mount 启用；实测只放 fragment 时 SF 报
#   Cannot find entry …configstore@1.0::ISurfaceFlingerConfigs）。
import re as _re

VINTF_COPY = [
    (f"{SYS}/system/etc/vintf/manifest.xml", "system/manifest.xml"),
]
VENDOR_FRAGS = [
    f"{VEN}/etc/vintf/manifest.xml",
    f"{VEN}/etc/vintf/manifest.disabled/gbm_mesa.allocator@4.0.xml",
    f"{VEN}/etc/vintf/manifest.disabled/gbm_mesa.mapper@4.0.xml",
]
merged = ""
for frag in VENDOR_FRAGS:
    if not os.path.exists(frag):
        print("  vintf fragment 缺:", frag)
        continue
    with open(frag, encoding="utf-8") as fh:
        txt = fh.read()
    inner = _re.sub(r"(?s)^.*?<manifest[^>]*>", "", txt)
    inner = _re.sub(r"(?s)</manifest>\s*$", "", inner).strip()
    merged += inner + "\n"
if merged:
    # Android 13 的 screencap 走 **AIDL 服务名 `SurfaceFlingerAIDL`**；SF 只有在 VINTF 里声明了才会注册它，
    # 否则客户端会一直 "Since 'SurfaceFlingerAIDL' could not be found, trying to start it as a lazy AIDL
    # service"（我们没 init 的 lazy 机制）⇒ screencap 永久阻塞（实测）。
    # 原镜像的 system manifest 里 AIDL 条目为 0（实测）⇒ 这里补上。名字不确定，两个候选都写（未知项无害）。
    # ── configstore 1.0（关键！）──
    # 完整符号化的崩溃回溯实证：SF 在 **__libc_init 的静态初始化**里就走
    #   libSurfaceFlingerProp.so(sysprop::start_graphics_allocator_service)
    #   → configstore@1.0::ISurfaceFlingerConfigs::getService
    # 而 manifest 只声明了 1.1 ⇒ 1.0 查找被 VINTF 拒绝 ⇒ HIDL 客户端崩（SIGSEGV@0x1）⇒ SF 启动即死。
    # （HIDL 允许 1.1 服务满足 1.0 客户端，但**前提是 manifest 里声明了 1.0**。）
    merged += (
        '    <hal format="hidl">\n'
        '        <name>android.hardware.configstore</name>\n'
        '        <transport>hwbinder</transport>\n'
        '        <version>1.0</version>\n'
        '        <interface>\n'
        '            <name>ISurfaceFlingerConfigs</name>\n'
        '            <instance>default</instance>\n'
        '        </interface>\n'
        '    </hal>\n'
    )
    merged += (
        '    <hal format="hidl">\n'
        '        <name>vendor.waydroid.task</name>\n'
        '        <transport>hwbinder</transport>\n'
        '        <version>1.0</version>\n'
        '        <interface>\n'
        '            <name>IWaydroidTask</name>\n'
        '            <instance>default</instance>\n'
        '        </interface>\n'
        '    </hal>\n'
        '    <hal format="aidl">\n'
        '        <name>android.hardware.graphics.surfaceflinger</name>\n'
        '        <version>1</version>\n'
        '        <fqname>ISurfaceComposer/default</fqname>\n'
        '    </hal>\n'
        '    <hal format="aidl">\n'
        '        <name>android.gui</name>\n'
        '        <version>1</version>\n'
        '        <fqname>ISurfaceComposer/default</fqname>\n'
        '    </hal>\n'
    )
    body = '<?xml version="1.0" encoding="utf-8"?>\n<manifest version="1.0" type="device">\n' + merged + "</manifest>\n"
    # ⚠ 去重：主 manifest 里已经声明过的 HAL，fragment 或我们手工补的条目就不要重复写。
    # 实测教训：重复的 <hal> 条目会让 libvintf 判 manifest 非法 ⇒ **所有** HAL 查找失败
    # （症状：mapper@4.0 找不到 ⇒ libui GraphicBufferMapper 构造 LOG_ALWAYS_FATAL ⇒ SF 崩）。
    try:
        import xml.etree.ElementTree as _ET
        _root = _ET.fromstring(body)
        _seen, _dups = set(), 0
        for _h in list(_root.findall("hal")):
            _n = _h.find("name")
            _v = _h.find("version")
            _key = ((_n.text or "") if _n is not None else "", (_v.text or "") if _v is not None else "")
            if _key in _seen:
                _root.remove(_h)
                _dups += 1
            else:
                _seen.add(_key)
        if _dups:
            print("  vintf: 去重 %d 条重复 HAL 条目" % _dups)
        body = _ET.tostring(_root, encoding="unicode") + "\n"
    except Exception as _e:
        print("  vintf: 去重失败（保留原样）:", _e)
    dst = os.path.join(STAGE, "vendor/manifest.xml")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, "w", encoding="utf-8") as fh:
        fh.write(body)
    print("  vintf: 合并 %d 份 → vendor/manifest.xml（%d 字节）" % (len(VENDOR_FRAGS), len(body)))

for src, rel in VINTF_COPY:
    if not os.path.exists(src):
        print("  vintf 跳过（缺）:", src)
        continue
    dst = os.path.join(STAGE, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)
    print("  vintf: %s → %s" % (os.path.basename(src), rel))


# ── mapper / gralloc 别名（libui 的 passthrough 查找可能拼不同名字）──
# 实测：/data/abort.txt 里 "gralloc-mapper is missing"（libui GraphicBufferMapper FATAL），
# 而 vendor 里只有 <flavor>=minigbm_gbm_mesa 那套名字 ⇒ 这里把候选名都补上（复制，体积小）。
_alias_src = {
    "android.hardware.graphics.mapper@4.0-impl.minigbm_gbm_mesa.so": [
        "android.hardware.graphics.mapper@4.0-impl.so",
        "android.hardware.graphics.mapper@4.0-impl.gbm.so",
        "android.hardware.graphics.mapper@4.0-impl.minigbm.so",
        "mapper.minigbm_gbm_mesa.so",
        "mapper.gbm.so",
    ],
    "gralloc.minigbm_gbm_mesa.so": ["gralloc.minigbm.so", "gralloc.so"],
}
for _base, _alts in _alias_src.items():
    _found = None
    for _d in (hwdir_s, hwdir_v):
        _p = os.path.join(_d, _base)
        if os.path.exists(_p):
            _found = _p
            break
    if not _found:
        print("  别名跳过（缺实现）:", _base)
        continue
    for _a in _alts:
        for _d in (hwdir_s, hwdir_v):
            _dst = os.path.join(_d, _a)
            if not os.path.exists(_dst):
                shutil.copy2(_found, _dst)
    print("  别名已补: %s → %d 个候选名" % (_base, len(_alts)))

if os.path.exists(OUT):
    os.remove(OUT)
with tarfile.open(OUT, "w:gz") as t:
    for root, _, files in os.walk(STAGE):
        for f in files:
            full = os.path.join(root, f)
            t.add(full, arcname=os.path.relpath(full, STAGE))

print("  SONAME 链接:", extra)
print("  未压缩: %.1f MB" % (total / 1024 / 1024))
print("  tar.gz: %d B (%.1f MB)" % (os.path.getsize(OUT), os.path.getsize(OUT) / 1024 / 1024))
for root, _, files in os.walk(STAGE):
    for f in sorted(files)[:6]:
        full = os.path.join(root, f)
        print("    %8d  %s" % (os.path.getsize(full), os.path.relpath(full, STAGE)))
# （已删：mesa 时代的 dri/ 符号链接块 —— 随 mesa 一起去掉，否则是悬空链接，打包末尾列目录会报错）

