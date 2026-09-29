#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""x86 boot image 注入器（Windows 本地跑）：把 108 构建的 boot 组件 + 预置 oat 注入生产
initrd，并打两个补丁——init 的 -Ximage 镜像启用块、libart 验证器软失败补丁。

用法：python build_bootimg_inject.py [生产initrd] [构建产物initrd] [输出initrd]
默认：QemuGuest/x86guest/{art_initrd_x64_pristine.gz(未打补丁的生产 initrd),
      bootimg_artifacts.cpio.gz(108 构建产物，scp 回来), art_initrd_x64_bootimg.gz(输出)}
⚠ 生产 initrd 必须用 pristine 版（本库留档 art_initrd_x64_pristine.gz）：补丁后的 init
  里 JVM_EXTRA 行已变、libart 已补，重复注入会在第④步报「JVM_EXTRA 行不匹配」。
  108 重出 pristine：bash mk_x86_initrd.sh 后取 art_initrd_x64.gz（未注入版）。

背景（2026-09-29，verifier 修复全程）：
· 108 chroot 与 guest 内原生 dex2oat 都在 Runtime::CreateResolutionMethod 的 LinearAlloc
  首次分配 mmap ENOMEM abort（低 2GB 被 dex2oat 自身 2GB+64MB 预留占满，strace 无效）；
  唯一可行 = 108 上 qemu-x86_64-static 用户态仿真跑 dex2oat64（guest 地址空间由 qemu
  管理，MAP_32BIT 低址分配不再撞预留），boot 全量编译 exit=0。
· ART13 镜像加载：组件必须放 /system/framework/x86_64/（默认推导位），-Xbootclasspath-
  locations 不重定向镜像，正解 = CATCLAW_JVM_EXTRA 里 -Ximage:<15 组件全列>（BCP 同序）。
· ART13 验证器对 OLLVM 混淆的 spider dex 硬拒（register Drawable vs expected Drawable
  同名不同源误判）：-Xverify:{softfail,none} 被 ART13 无视（deprecated），debuggable 属性
  libart 不读；最终 = libart.so 的 ClassVerifier::VerifyClass 入口补丁，恒返 kSoftFailure
  （类照常加载、走解释器），boot image 的编译代码不受影响。"""
import gzip, io, os, sys

Z = r"d:/Code/CatClawVideo/CatClawVideo.Maui/QemuGuest/x86guest"
BAK = os.path.join(Z, "art_initrd_x64_pristine.gz")
NEW = os.path.join(Z, "bootimg_artifacts.cpio.gz")
OUT = os.path.join(Z, "art_initrd_x64_bootimg.gz")
THUNDER = os.path.join(Z, "thunder_assets.cpio.gz")  # 磁力资产（可选，缺=不带磁力）
if len(sys.argv) == 4:
    BAK, NEW, OUT = sys.argv[1:4]

# ── cpio newc 读写 ────────────────────────────────────────────────────────────
def read_cpio_newc(data):
    entries, pos = [], 0
    while pos < len(data) - 6:
        if data[pos:pos+6] != b"070701":
            pos += 1
            continue
        hdr = data[pos:pos+110]
        f = lambda i: int(hdr[6+i*8:6+(i+1)*8], 16)
        (ino, mode, uid, gid, nlink, mtime, filesize, _dmaj, _dmin, _rmaj, _rmin,
         namesize, _chk) = (f(0), f(1), f(2), f(3), f(4), f(5), f(6), f(7), f(8),
                            f(9), f(10), f(11), f(12))
        name = data[pos+110:pos+110+namesize-1].decode("utf-8")
        dpos = (pos + 110 + namesize + 3) & ~3
        fdata = data[dpos:dpos+filesize]
        pos = (dpos + filesize + 3) & ~3
        if name == "TRAILER!!!":
            break
        link = fdata.decode("utf-8") if (mode & 0o170000) == 0o120000 else ""
        entries.append((name, mode, uid, gid, nlink, mtime, fdata, link))
    return entries

def pad4(out):
    while out.tell() % 4:
        out.write(b"\0")

def write_cpio_newc(entries, out):
    ino = 300000
    for (name, mode, uid, gid, nlink, mtime, fdata, link) in entries:
        ino += 1
        data = link.encode("utf-8") if link else fdata
        namesize = len(name.encode("utf-8")) + 1
        out.write(b"070701" + b"".join(
            b"%08X" % v for v in (ino, mode, uid, gid, nlink, mtime, len(data),
                                  0, 0, 0, 0, namesize, 0)))
        out.write(name.encode("utf-8") + b"\0")
        pad4(out)
        out.write(data)
        pad4(out)
    # TRAILER 头 = magic + 12 个零字段 + namesize(11) + chksum(0)（新头共 110 字节）
    out.write(b"070701" + b"0" * 88 + b"0000000B" + b"00000000" + b"TRAILER!!!\0")
    pad4(out)

# ── libart 验证器补丁 ────────────────────────────────────────────────────────
VERIFY_CLASS_VADDR = 0x871f40   # art::verifier::ClassVerifier::VerifyClass（Waydroid ART13 libart.so）
# 恒返 kNoFailure(0)：类直接标记 verified 走正常装载路径。⚠ 不能返 kSoftFailure(1)——
# 软失败路径要求验证器先填好的副作用（dex cache / 状态机）缺失时 SIGSEGV（2026-09-29 实测）。
SOFTFAIL = bytes.fromhex("b800000000c3")  # mov eax,0(=kNoFailure) ; ret

def patch_libart(data):
    e_phoff = int.from_bytes(data[0x20:0x28], "little")
    e_phentsize = int.from_bytes(data[0x36:0x38], "little")
    e_phnum = int.from_bytes(data[0x38:0x3a], "little")
    off = None
    for i in range(e_phnum):
        p = e_phoff + i * e_phentsize
        if int.from_bytes(data[p:p+4], "little") == 1:  # PT_LOAD
            po = int.from_bytes(data[p+8:p+16], "little")
            pv = int.from_bytes(data[p+16:p+24], "little")
            pf = int.from_bytes(data[p+32:p+40], "little")
            if pv <= VERIFY_CLASS_VADDR < pv + pf:
                off = po + (VERIFY_CLASS_VADDR - pv)
                break
    if off is None:
        raise SystemExit("!! libart: VerifyClass vaddr 未落在任何 LOAD 段")
    if data[off:off+6] == SOFTFAIL:
        print("   libart 已是补丁版，跳过")
        return data
    orig = data[off:off+6]
    data[off:off+6] = SOFTFAIL
    print("   libart VerifyClass @ %#x：%s → mov eax,1;ret" % (off, orig.hex()))
    return data

def main():
    print("① 解包生产 initrd ...")
    with gzip.open(BAK, "rb") as g:
        prod = read_cpio_newc(g.read())
    print("   条目:", len(prod))

    print("② 解包构建产物 initrd ...")
    with gzip.open(NEW, "rb") as g:
        new_entries = read_cpio_newc(g.read())

    # 组件放 /system/framework/x86_64/（ART13 默认推导位）；javalib 副本不注入（省 RAM）
    inject = []
    for e in new_entries:
        if e[0].startswith("system/javalib/x86_64/"):
            inject.append((e[0].replace("system/javalib/x86_64/", "system/framework/x86_64/", 1),
                           e[1], e[2], e[3], e[4], e[5], e[6], e[7]))
        elif e[0].startswith("data/dalvik-cache/x86_64/"):
            inject.append(e)
    # 框架根 boot.art 软链（默认推导位的镜像 location 本体，aarch64 同款布局）
    inject.append(("system/framework/boot.art", 0o120777, 0, 0, 1, 0, b"", "x86_64/boot.art"))

    prod_names = {e[0] for e in prod}
    need_dirs = set()
    for e in inject:
        p = e[0]
        while "/" in p:
            p = p.rsplit("/", 1)[0]
            need_dirs.add(p)
    for d in sorted(need_dirs):
        if d not in prod_names:
            inject.insert(0, (d, 0o040755, 0, 0, 2, 0, b"", ""))
    inject = [e for e in inject if e[0] not in prod_names
              or e[0].startswith(("system/framework/x86_64/", "data/dalvik-cache/x86_64/"))
              or e[0] == "system/framework/boot.art"]

    # libart 软失败补丁：⚠ 2026-09-29 晚实测 kSoftFailure/kNoFailure 两个变体都在
    # 「未验证类执行」时 SIGSEGV（libart+0x163dbc，与 JIT 开关无关）——补丁跳过了
    # VerifyClass 的调用方契约副作用。默认关闭，待符号化定位正确挂钩点后再启用。
    patched = None
    if os.environ.get("PATCH_LIBART") == "1":
        for i, e in enumerate(inject):
            if e[0] == "apex/com.android.art/lib64/libart.so":
                inject[i] = e[:6] + (patch_libart(bytearray(e[6])),) + e[7:]
                patched = True
        if patched is None:  # 生产树里的 libart 打补丁
            merged_tmp = [(e[0], e) for e in prod]
            for i, (n, e) in enumerate(merged_tmp):
                if n == "apex/com.android.art/lib64/libart.so":
                    merged_tmp[i] = (n, e[:6] + (patch_libart(bytearray(e[6])),) + e[7:])
                    patched = True
            prod = [e for _, e in merged_tmp]
    print("③ libart 补丁:", "已应用" if patched else "跳过（PATCH_LIBART=1 启用）")
    print("③ 注入条目:", len(inject),
          "| framework 组件:", len([e for e in inject if e[0].startswith("system/framework/x86_64/")]),
          "| 缓存:", len([e for e in inject if e[0].startswith("data/dalvik-cache/x86_64/")]))

    merged = [e for e in prod if e[0] not in {i[0] for i in inject}] + inject

    # init 补丁：-Ximage 全列覆盖（BCP 15 项同序）+ -Xnorelocate
    init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
    init = merged[init_i][6]
    old_line = b'export CATCLAW_JVM_EXTRA="-Xnoimage-dex2oat -Xnodex2oat -Xcheck:jni"'
    if old_line not in init:
        raise SystemExit("!! init 的 JVM_EXTRA 行不匹配（init 模板变了？）")
    FWX = "/system/framework/x86_64/"
    locs = ":".join(FWX + n for n in (
        "boot.art", "boot-core-libart.art", "boot-core-icu4j.art", "boot-okhttp.art",
        "boot-bouncycastle.art", "boot-apache-xml.art", "boot-conscrypt.art",
        "boot-framework.art", "boot-ext.art", "boot-telephony-common.art",
        "boot-voip-common.art", "boot-ims-common.art", "boot-android.hidl.base-V1.0-java.art",
        "boot-android.hidl.manager-V1.0-java.art", "boot-android.test.base.art"))
    new_line = ('export CATCLAW_JVM_EXTRA="-Xnorelocate -Xcheck:jni '
                '-Ximage:' + locs + '"\n'
                '#（-Ximage 整体覆盖 ART13 默认镜像 spec —— 那个 spec 是 AOSP boot.art+\n'
                '#  boot-framework.art+双 prof 布局，与我们的 multi-image 组件集对不上；\n'
                '#  -Xbootclasspath-locations 不重定向镜像，2026-09-29 三轮实测唯一可行）').encode("utf-8")
    init = init.replace(old_line, new_line)
    merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
    print("④ init 已打 bootimg 补丁（-Xnorelocate + -Ximage 15 组件全列）")

    # ── 磁力资产（可选）：ARM harness + qemu-aarch64-static + 引擎库 + init 迅雷段 ──
    if os.path.exists(THUNDER):
        print("④b 合并磁力资产 ...")
        with gzip.open(THUNDER, "rb") as g:
            thunder_entries = read_cpio_newc(g.read())
        tn = {e[0] for e in thunder_entries}
        merged = [e for e in merged if e[0] not in tn] + thunder_entries
        print("   磁力条目:", len(thunder_entries))
        if b"[thunder]" not in init:
            thunder_sh = (
                '\n# ── 迅雷段（合并磁力）：cmdline thunderport= 存在时拉起 ARM harness ──\n'
                '# ARM harness + 迅雷 SDK（只有 ARM 版）在 x86 guest 里经 qemu-aarch64-static\n'
                '# 用户态转译跑（108 已实测：引擎初始化/BT 边下边播全链路通）。ndk_translation\n'
                '# 路线（expA）harness 秒退不可用。thunderport 缺省时本段整体休眠（纯桥 VM 零开销）。\n'
                'TP=$(getarg thunderport)\n'
                'if [ -n "$TP" ] && [ -x /harness ]; then\n'
                '    export CTRL_HOST="10.0.2.2"          # SLIRP 宿主侧（控制端 QemuGuestEngine）\n'
                '    export CTRL_PORT="$TP"\n'
                '    export PROXY_PORT="20080"            # guest 媒体代理口（宿主 hostfwd -:20080 对准它）\n'
                '    export P2SP_SECS="0" DL_SECS="0"\n'
                '    export BLK_DEV="$(getarg blkdev)"    # 数据面块设备；缺位时 harness 回退纯转发\n'
                '    export QCO="${QCO:-1}"\n'
                '    # ARM bionic 引擎库优先（x86 的 /system/lib64 是错误架构，bionic 会跳过继续找）\n'
                '    export LD_LIBRARY_PATH=/data/catclaw/art/lib:/thunder-arm/system/lib64\n'
                '    SD=$(getarg swapdev)\n'
                '    if [ -n "$SD" ] && [ -b "$SD" ]; then\n'
                '        $BB mkswap "$SD" 2>/dev/null\n'
                '        $BB swapon "$SD" 2>/dev/null && echo "[thunder] swap on $SD"\n'
                '    fi\n'
                '    i=0\n'
                '    while [ $i -lt 30 ] && ! $BB ifconfig eth0 2>/dev/null | $BB grep -q 10.0.2.15; do\n'
                '        $BB sleep 1; i=$((i+1))\n'
                '    done\n'
                '    $BB mkdir -p /thunder-data 2>/dev/null\n'
                '    # 监督循环：harness 退出（崩溃/宿主 EXIT 重置）→ 2s 后拉起；引擎任务表随进程清空，\n'
                '    # /thunder-data 是 VM 级 tmpfs、块设备数据在宿主镜像 —— 都不随进程死。\n'
                '    (\n'
                '      while true; do\n'
                '        /qemu-aarch64-static -L /thunder-arm /harness >>/thunder.log 2>&1\n'
                '        echo "[thunder] harness 退出（code=$?），2s 后重启（引擎任务表清空）"\n'
                '        $BB sleep 2\n'
                '      done\n'
                '    ) &\n'
                '    echo "[thunder] harness 监督循环已起（CTRL_PORT=$TP BLK_DEV=${BLK_DEV:-无}，日志 /thunder.log）"\n'
                'fi\n').encode("utf-8")
            anchor = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
            if anchor not in init:
                raise SystemExit("!! init 的 artlaunch 锚点未找到，迅雷段插入失败")
            init = init.replace(anchor, thunder_sh + b"\n" + anchor, 1)
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("   init 迅雷段已插入（qemu-aarch64 转译 harness）")
    else:
        print("④b （无磁力资产，跳过 thunder 注入）")

    print("⑤ 重打包 ...")
    buf = io.BytesIO()
    write_cpio_newc(merged, buf)
    with gzip.open(OUT, "wb", compresslevel=1) as g:
        g.write(buf.getvalue())
    print("产物:", OUT, round(os.path.getsize(OUT) / 1048576, 1), "MB")
    print("ALL-DONE")

main()
