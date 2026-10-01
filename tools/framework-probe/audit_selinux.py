#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针 4｜SELinux 与属性区审计（只读）。

要回答的问题（T4 §四.5）：**「`__system_property_area_init` 失败是因为没有 SELinux 策略」这条
既有结论还站着吗**，以及"关掉 SELinux 能不能绕过框架前置"。

为什么值得单独查：这条结论决定路线 (A) 与 (B) 的取舍 —— 如果属性区失败与 SELinux 无关，
那"必须先补 SELinux 策略"这个最大的一块工作量就直接消失了；反之 shim 路线要一直背到策略编译。
所以这里既做静态盘点（策略文件在不在、secilc 在不在），也给一条**能推翻它的实验**。

用法：python audit_selinux.py [--source <initrd|dir>] [--log <qemu-console-art.log>]
"""
import argparse
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cpioimg as C

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_IMG = os.path.normpath(os.path.join(
    HERE, "..", "..", "CatClawVideo.Maui", "QemuGuest", "x86guest", "art_initrd_x64.gz"))
DEFAULT_LOG = os.path.expandvars(r"%LOCALAPPDATA%\CatClawVideo.debug\qemu-console-art.log")

ARTIFACTS = [
    ("system/etc/selinux/plat_sepolicy.cil", "平台策略（CIL 源码，init 要编译才能加载）"),
    ("system/etc/selinux/precompiled_sepolicy", "已编译策略（有它 init 就不用跑 secilc）"),
    ("system/etc/selinux/plat_file_contexts", "文件上下文（restorecon 依赖）"),
    ("system/etc/selinux/plat_property_contexts", "属性上下文（**属性写权限模型**）"),
    ("system/etc/selinux/plat_service_contexts", "服务上下文（servicemanager 注册检查）"),
    ("system/etc/selinux/mapping", "平台映射（version→version 的 cil）"),
    ("system/bin/secilc", "CIL 编译器（无 precompiled 时 init 在开机跑它）"),
    ("system/bin/init", "真 Android init（我们镜像里其实有）"),
    ("dev/__properties__", "属性区目录（bionic 找旧式 per-property 文件时用）"),
    ("system/etc/property_info", "property_info（客户端侧写检查）"),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default=DEFAULT_IMG)
    ap.add_argument("--prefix", default="")
    ap.add_argument("--log", default=DEFAULT_LOG)
    a = ap.parse_args()

    want = lambda n: ("selinux" in n.lower() or "sepolicy" in n.lower() or n.endswith(".rc")
                      or "__properties__" in n or "property_info" in n)
    ents, fp = C.load(a.source, want=want, prefix=a.prefix)
    print("== audit_selinux ==")
    print("来源: %s" % fp)

    have = collections.defaultdict(list)
    for e in ents:
        for path, why in ARTIFACTS:
            if e.name == path or e.name.endswith("/" + path) or e.name == path.replace("system/", ""):
                have[path].append(e)
    rows = []
    for path, why in ARTIFACTS:
        es = have.get(path, [])
        if not es:
            rows.append((path, "缺", "-", why))
        else:
            e = es[0]
            rows.append((path, "在" if C.is_reg(e.mode) else "目录",
                         ("%d KiB" % (e.size // 1024)) if C.is_reg(e.mode) else "%d 条" %
                         sum(1 for x in es), why))
    C.table(rows, ["产物", "状态", "大小/条目", "为什么框架要它"], "SELinux / 属性区产物盘点")

    maps = [e for e in ents if "/etc/selinux/mapping/" in e.name and e.name.endswith(".cil")]
    print("  mapping/*.cil: %d 个，合计 %d KiB" % (len(maps), sum(e.size for e in maps) // 1024))
    vend = [e for e in ents if e.name.startswith("vendor/etc/selinux")]
    print("  vendor 侧 selinux 条目: %d %s" % (len(vend), [e.name for e in vend][:4]))

    # VINTF：framework 侧的 HAL 声明检查（矩阵条目不够 ⇒ system_server 起不来时报 missing HAL）
    vin = sorted((e for e in ents if "/etc/vintf" in e.name and C.is_reg(e.mode)),
                 key=lambda e: -e.size)
    print("  VINTF 条目 %d 个：%s" % (len(vin), [(e.name, e.size) for e in vin[:6]]))
    print("  libvintf.so: %s" % ([e.size for e in ents if e.name.endswith("libvintf.so")] or "缺"))

    # rc 里与 SELinux 相关的动作（真 init 路线会执行它们）
    seclabel = enforce = restorecon = 0
    for e in ents:
        if not e.data or not e.name.endswith(".rc"):
            continue
        t = e.text
        seclabel += len(re.findall(r"(?m)^\s*seclabel\s+", t))
        enforce += t.count("/sys/fs/selinux/enforce")
        restorecon += len(re.findall(r"restorecon", t))
    print("  rc 里：seclabel 行 %d、写 /sys/fs/selinux/enforce %d、restorecon %d"
          % (seclabel, enforce, restorecon))

    # 运行时事实：guest 控制台日志里到底有没有 denial
    if a.log and os.path.exists(a.log):
        pat = collections.Counter()
        lines = 0
        with open(a.log, encoding="utf-8", errors="replace") as f:
            for ln in f:
                lines += 1
                for k in ("avc:  denied", "avc: denied", "SELinux", "selinux",
                          "property area", "__system_property", "Failed to set", "permissive",
                          "Dalvik cache", "dalvik-cache", "apex", "zygote", "servicemanager"):
                    if k in ln:
                        pat[k] += 1
        print("  运行日志 %s：%d 行，命中 %s" %
              (os.path.basename(a.log), lines, dict(pat.most_common(8)) or "一条都没命中"))
        if lines:
            print("  （⚠ 这是**当前这次启动**的日志；判「没有 denial」之前要确认日志覆盖到了框架尝试启动的那段）")
    else:
        print("  运行日志: 未找到 %s（跳过运行时对照）" % a.log)

    print("\n结论与判读:")
    print("  · 平台策略**文件在**（plat_sepolicy.cil 存在）但 precompiled_sepolicy 缺 ⇒ 走真 init 时")
    print("    要么开机跑 secilc（镜像里有 secilc，TCG 下耗时需实测），要么用 `androidboot.selinux=permissive`")
    print("    / `--skip-sepolicy`（init 支持吗要在第一步实验里验，别猜）。")
    print("  · 运行日志里 denial 计数见上：若为 0 且我们当前**根本没加载策略**，那"
          "「属性区失败因为缺 SELinux」这条就**只是巧合**，需要下面的实验来判。")
    print("  · 第一步可验证实验（只读、不改镜像；由 T1 注入或经 adb shell 手跑）：")
    print("      在 guest 里执行 `propinit`（blobs/propfix）并抓 bionic 的 libc 日志行 ——")
    print("      bionic 在 area_init 失败时会把**具体原因**写进 log（`failed to initialize system"
          " properties area` + errno/路径）。")
    print("      判读：出现 EPERM/`/sys/fs/selinux`/`u:object_r:` ⇒ SELinux 假设成立；")
    print("            出现 EEXIST/`already`/`/dev/__properties__` 路径类 ⇒ 是**目录或重复初始化**问题，"
          "与 SELinux 无关，路线 (B) 的属性服务成本骤降。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
