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
# 系统镜像也要挂：libvulkan.so（Vulkan loader）在 /system/lib64 里。
# ⚠ 实测教训：缺了它 ANGLE 直接 "no suitable EGLConfig found, giving up"（因为根本没有 Vulkan loader）。
SYS = "/mnt/b11sys"
os.makedirs(SYS, exist_ok=True)
if not os.path.ismount(SYS):
    subprocess.run(["mount", "-o", "ro,loop", "/var/lib/waydroid/images/system.img", SYS], check=False)
if not os.path.ismount(SYS):
    print("  !! 警告：系统镜像未挂上 ⇒ libvulkan.so 会缺（ANGLE 会直接放弃）")

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
    # ⚠ 但 **GBM 必须**有它：minigbm 的 gbm 会加载 dri/<driver>_dri.so，其本体就是 libgallium_dri.so。
    #   当初改走 ANGLE 时把它一起删了 ⇒ gbm_create_device 直接 ENOENT(2) ⇒ mapper 返回 NO_RESOURCES（本轮实证）。
    #   GBM 分配与 GL 走哪条路无关，所以这个必须有。
    ("lib64/libgallium_dri.so",        "system/lib64/libgallium_dri.so"),
    ("lib64/libgbm_mesa.so",           "system/lib64/libgbm_mesa.so"),
    ("lib64/libgbm_mesa_wrapper.so",   "system/lib64/libgbm_mesa_wrapper.so"),
    ("lib64/dri_gbm.so",               "system/lib64/dri_gbm.so"),
    ("lib64/libminigbm_gralloc_gbm_mesa.so", "system/lib64/libminigbm_gralloc_gbm_mesa.so"),
    # ── vendor 副本（2026-10-02）：minigbm 后端插件搜索 /vendor/lib64/，108 参照系实锤
    #    vendor/lib64 同时有 dri_gbm.so + libgallium_dri.so —— 缺了 gbm_create_device ENOENT
    ("lib64/libgallium_dri.so",        "vendor/lib64/libgallium_dri.so"),
    ("lib64/dri_gbm.so",               "vendor/lib64/dri_gbm.so"),
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
    # lavapipe 硬依赖真 LLVM（100MB，gzip 约 31.5MB）：符号桩不行（bionic 加载期要求全部符号可解析，
    # 而桩会把 lavapipe 自己的 LLVM 调用指到假地址 ⇒ SIGSEGV，实测）。系统自带的 libLLVM_android.so
    # 只有 27MB 但覆盖率 177/211（缺 JIT/DI 那批 C API）⇒ 也不行。所以只能带真身。
    ("lib64/libLLVM22.so",              "system/lib64/libLLVM22.so"),
    ("lib64/hw/vulkan.virtio.so",       "system/lib64/hw/vulkan.virtio.so"),
    ("lib64/hw/vulkan.virtio.so",       "system/lib64/vulkan.virtio.so"),
    (SYS + "/system/lib64/libvulkan.so", "system/lib64/libvulkan.so"),
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

if os.path.exists(OUT):
    os.remove(OUT)
DRIVERS = ["swrast", "llvmpipe", "iris", "virtio_gpu", "kms_swrast", "zink"]
with tarfile.open(OUT, "w:gz") as t:
    for root, _, files in os.walk(STAGE):
        for f in files:
            full = os.path.join(root, f)
            t.add(full, arcname=os.path.relpath(full, STAGE))
    # ── dri/ 软链直接写进 tar（LNKTYPE，2026-10-02）──
    # init 里 ln -sf 不可靠（实测 6 个只成功 1 个：busybox 行为/字符串转义），打包期写死最稳。
    # 相对目标 ../libgallium_dri.so 在 system/lib64 与 vendor/lib64 两侧各自解析正确。
    for base_dir in ("system/lib64/dri", "vendor/lib64/dri"):
        for n in DRIVERS:
            ti = tarfile.TarInfo(name="%s/%s_dri.so" % (base_dir, n))
            ti.type = tarfile.LNKTYPE
            ti.linkname = "../libgallium_dri.so"
            ti.mode = 0o755
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = "root"
            ti.mtime = 0
            t.addfile(ti)
print("  tar 内 dri 软链: %d 个" % (len(DRIVERS) * 2))

print("  未压缩: %.1f MB" % (total / 1024 / 1024))
print("  tar.gz: %d B (%.1f MB)" % (os.path.getsize(OUT), os.path.getsize(OUT) / 1024 / 1024))
for root, _, files in os.walk(STAGE):
    for f in sorted(files):
        full = os.path.join(root, f)
        print("    %10d  %s" % (os.path.getsize(full), os.path.relpath(full, STAGE)))
# （已删：mesa 时代的 dri/ 符号链接块 —— 随 mesa 一起去掉，否则是悬空链接，打包末尾列目录会报错）

