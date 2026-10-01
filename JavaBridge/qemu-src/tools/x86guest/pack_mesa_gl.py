#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""B1.1: 只打 mesa 软件 GL（llvmpipe）那一小组文件 —— **不做递归闭包**。

理由（实测）：mesa 各库的 NEEDED 全是 bionic 基础库（libc/libm/libdl/libdrm/libcutils/
libc++/liblog/libnativewindow/libsync/libhardware/libhidlbase/libutils/libgralloctypes），
这些 guest 里都有；而递归闭包会拖进一个 105MB 的大家伙（把包从 ~50MB 未压缩吹到 165MB）。
"""
import os, shutil, subprocess, tarfile

VEN = "/mnt/b11ven"
STAGE = "/root/b1_blobs/mstage"
OUT = "/root/b1_blobs/mesa-gl.tar.gz"

os.makedirs(VEN, exist_ok=True)
if not os.path.ismount(VEN):
    subprocess.run(["mount", "-o", "ro,loop", "/root/x86guest/vendor-ex/vendor.img", VEN], check=False)
if not os.path.ismount(VEN):
    raise SystemExit("!! vendor 未挂载")

FILES = [
    # 已删：mesa 的 GL（实测 loader 拒绝接管、直接调也 init=0；改走 ANGLE）
    # 已删：mesa 的 GL（实测 loader 拒绝接管、直接调也 init=0；改走 ANGLE）
    # 已删：mesa 的 GL（实测 loader 拒绝接管、直接调也 init=0；改走 ANGLE）
    # ⚠ 用**打过补丁**的副本：原始 libgallium_dri.so 硬依赖 libLLVM22.so（未压缩 105MB），
    # dlopen libEGL_mesa 会直接失败（实测："library libLLVM22.so not found"）。
    # llvmpipe 需要 LLVM，但同一 mega-driver 里的 swrast(softpipe) 不需要 ⇒
    # patchelf --remove-needed libLLVM22.so，运行时用 GALLIUM_DRIVER=swrast。
    # 补丁脚本见 .zwork/b11_nollvm.sh；产物 /root/b1_blobs/libgallium_nollvm.so。
    # 已删：mesa 的 GL（实测 loader 拒绝接管、直接调也 init=0；改走 ANGLE）
    ("lib64/libgbm_mesa.so",           "system/lib64/libgbm_mesa.so"),
    ("lib64/libgbm_mesa_wrapper.so",   "system/lib64/libgbm_mesa_wrapper.so"),
    ("lib64/dri_gbm.so",               "system/lib64/dri_gbm.so"),
    ("lib64/libminigbm_gralloc_gbm_mesa.so", "system/lib64/libminigbm_gralloc_gbm_mesa.so"),
    # gallium 链接了各家的 DRM 后端；缺一个 dlopen 就直接失败
    # （实测：只有 libdrm_intel.so 缺失时报 "library \"libdrm_intel.so\" not found"）。
    ("lib64/libdrm_intel.so",   "system/lib64/libdrm_intel.so"),
    ("lib64/libdrm_amdgpu.so",  "system/lib64/libdrm_amdgpu.so"),
    ("lib64/libdrm_radeon.so",  "system/lib64/libdrm_radeon.so"),
    ("lib64/libdrm.so",         "system/lib64/libdrm.so"),
    # ── ANGLE + 软件 Vulkan（lavapipe）──
    # mesa 那条路在本 guest 上走不通（loader 加载了却不接管；直接调 mesa 的 EGL 也 init=0）。
    # 改走 ANGLE→Vulkan→lavapipe 的全软件组合（Android 上 ANGLE 默认就是 Vulkan 后端）。
    ("lib64/egl/libEGL_angle.so",       "system/lib64/egl/libEGL_angle.so"),
    ("lib64/egl/libGLESv2_angle.so",    "system/lib64/egl/libGLESv2_angle.so"),
    ("lib64/egl/libGLESv1_CM_angle.so", "system/lib64/egl/libGLESv1_CM_angle.so"),
    ("lib64/hw/vulkan.lvp.so",          "system/lib64/hw/vulkan.lvp.so"),
    ("lib64/hw/vulkan.lvp.so",          "system/lib64/vulkan.lvp.so"),
    ("lib64/hw/vulkan.virtio.so",       "system/lib64/hw/vulkan.virtio.so"),
    ("lib64/hw/vulkan.virtio.so",       "system/lib64/vulkan.virtio.so"),
    ("/mnt/b11sys/system/lib64/libvulkan.so", "system/lib64/libvulkan.so"),
]

shutil.rmtree(STAGE, ignore_errors=True)
total = 0
for src, rel in FILES:
    p = src if src.startswith("/") else os.path.join(VEN, src)
    if not os.path.exists(p):
        print("  跳过（缺）:", src)
        continue
    dst = os.path.join(STAGE, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(p, dst)
    total += os.path.getsize(dst)
    # 同名软链：bionic 也按 SONAME 找
    dirn, base = os.path.split(dst)
    soname = os.path.basename(os.path.realpath(p))
    if base != soname:
        link = os.path.join(dirn, soname)
        if not os.path.lexists(link):
            os.symlink(base, link)

# mesa 的驱动按 dri/<name>_dri.so 找（108 上实测过同一坑）
dri = os.path.join(STAGE, "system/lib64/dri")   # system 命名空间
os.makedirs(dri, exist_ok=True)
for n in ("iris_dri.so", "llvmpipe_dri.so", "swrast_dri.so"):
    os.symlink("../libgallium_dri.so", os.path.join(dri, n))

# ── 同时挂到 /vendor/lib64（sphal 命名空间）──
# 根因（实测）：SF 是 system 进程，它拿到的 EGL display 自述为
#   "1.4 Android META-EGL"
# 即 libEGL.so 这个 loader **没能加载任何驱动**，回落成内置空壳，于是 eglChooseConfig 一个配置都不给。
# loader 在 sphal 命名空间里找 /vendor/lib64/egl/libEGL_<driver>.so ⇒ 这里用**符号链接**
# 指向 /system/lib64 下的真身（零体积代价；绝对路径在同一进程里可达）。
vlinks = [
    ("vendor/lib64/egl/libEGL_mesa.so",       "../../../system/lib64/egl/libEGL_mesa.so"),
    ("vendor/lib64/egl/libGLESv2_mesa.so",    "../../../system/lib64/egl/libGLESv2_mesa.so"),
    ("vendor/lib64/egl/libGLESv1_CM_mesa.so", "../../../system/lib64/egl/libGLESv1_CM_mesa.so"),
    ("vendor/lib64/libgallium_dri.so",        "../../system/lib64/libgallium_dri.so"),
    ("vendor/lib64/libgbm_mesa.so",           "../../system/lib64/libgbm_mesa.so"),
    ("vendor/lib64/libgbm_mesa_wrapper.so",   "../../system/lib64/libgbm_mesa_wrapper.so"),
    ("vendor/lib64/dri_gbm.so",               "../../system/lib64/dri_gbm.so"),
    ("vendor/lib64/libdrm.so",                "../../system/lib64/libdrm.so"),
    ("vendor/lib64/libdrm_intel.so",          "../../system/lib64/libdrm_intel.so"),
    ("vendor/lib64/libdrm_amdgpu.so",         "../../system/lib64/libdrm_amdgpu.so"),
    ("vendor/lib64/libdrm_radeon.so",         "../../system/lib64/libdrm_radeon.so"),
    ("vendor/lib64/libLLVM22.so",             "../../system/lib64/libLLVM22.so"),
]
for rel, target in vlinks:
    full = os.path.join(STAGE, rel)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    if not os.path.lexists(full):
        os.symlink(target, full)
if os.path.exists(OUT):
    os.remove(OUT)
with tarfile.open(OUT, "w:gz") as t:
    for root, _, files in os.walk(STAGE):
        for f in files:
            full = os.path.join(root, f)
            t.add(full, arcname=os.path.relpath(full, STAGE))

print("  未压缩: %.1f MB" % (total / 1024 / 1024))
print("  tar.gz: %d B (%.1f MB)" % (os.path.getsize(OUT), os.path.getsize(OUT) / 1024 / 1024))
for root, _, files in os.walk(STAGE):
    for f in sorted(files):
        full = os.path.join(root, f)
        print("    %10d  %s" % (os.path.getsize(full), os.path.relpath(full, STAGE)))
