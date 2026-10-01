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

# ★ nterp 禁用补丁（2026-09-29 深夜定案）：磁力 spider 的 OLLVM 字节码在 ART13 nterp
#   快速解释器下必 SIGSEGV（nterp_op_invoke_virtual，崩点 0x363dbc），aarch64 的 ART9
#   无 nterp 故无此问题。IsNterpSupported/CanRuntimeUseNterp 恒返 false → 全 guest 回落
#   mterp 参考解释器（aarch64 同语义），OLLVM 码随便跑。两个开关都关（双保险）。
NTERP_OFF_VADDRS = (0x925c40, 0x925c50)  # IsNterpSupported / CanRuntimeUseNterp
RETURN_FALSE = bytes.fromhex("b800000000c3")  # mov eax,0 ; ret

def vaddr_to_off(data, vaddr):
    e_phoff = int.from_bytes(data[0x20:0x28], "little")
    e_phentsize = int.from_bytes(data[0x36:0x38], "little")
    e_phnum = int.from_bytes(data[0x38:0x3a], "little")
    for i in range(e_phnum):
        p = e_phoff + i * e_phentsize
        if int.from_bytes(data[p:p+4], "little") == 1:  # PT_LOAD
            po = int.from_bytes(data[p+8:p+16], "little")
            pv = int.from_bytes(data[p+16:p+24], "little")
            pf = int.from_bytes(data[p+32:p+40], "little")
            if pv <= vaddr < pv + pf:
                return po + (vaddr - pv)
    return None

def patch_libart(data):
    # ① nterp 禁用（磁力 OLLVM 码的 SIGSEGV 根因）
    for va in NTERP_OFF_VADDRS:
        off = vaddr_to_off(data, va)
        if off is None:
            raise SystemExit("!! libart: nterp 开关 %#x 未落在 LOAD 段" % va)
        if data[off:off+6] == RETURN_FALSE:
            print("   libart nterp 开关 %#x 已是补丁版" % va)
            continue
        orig = data[off:off+6]
        data[off:off+6] = RETURN_FALSE
        print("   libart nterp 开关 @ %#x（vaddr %#x）：%s → mov eax,0;ret" % (off, va, orig.hex()))
    # ② 验证器软失败补丁（默认不用；PATCH_LIBART=1 实验时打开——见 VERIFY_CLASS_VADDR 注释）
    if os.environ.get("PATCH_VERIFY") == "1":
        off = vaddr_to_off(data, VERIFY_CLASS_VADDR)
        if data[off:off+6] != SOFTFAIL:
            data[off:off+6] = SOFTFAIL
            print("   libart VerifyClass（实验）@ %#x 已打软失败补丁" % off)
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

    # libart 补丁：① nterp 禁用（无条件——磁力 OLLVM 码 SIGSEGV 的根因修复）
    # ② VerifyClass 软失败（实验性，PATCH_VERIFY=1 才启用）
    patched = None
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
    print("③ libart 补丁:", "已应用（nterp 禁用" + ("+VerifyClass 实验)" if os.environ.get("PATCH_VERIFY") == "1" else ")") if patched else "未找到 libart 条目")
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

    # ── 调试后门（排障用，可撤）：反向 shell——guest 拨出 10.0.2.2:18777 供排障/dump ──
    # 2026-10-01：改为 **opt-in** —— 后门是 while 循环每 2s 重试 10.0.2.2:18777，
    # 没有监听时会在 guest 控制台刷出 795 次 nc: can't connect to remote host（实测），
    # 既淹没有效日志、又是个常开的后门。现在只有显式设 DBGPORT=<非0> 才插入。
    if os.environ.get("DBGPORT") not in (None, "", "0"):
        bd_sh = (
            '\n# ── 调试后门（排障用，可撤）：反向 shell（硬编码 18777）──\n'
            '(while true; do\n'
            '    rm -f /tmp/bdf; mkfifo /tmp/bdf\n'
            '    $BB nc 10.0.2.2 18777 < /tmp/bdf | $BB sh > /tmp/bdf 2>&1\n'
            '    $BB sleep 2\n'
            'done) &\n'
            'echo "[dbg] 反向后门已起（10.0.2.2:18777）"\n').encode("utf-8")
        anchor2 = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
        if "调试后门".encode("utf-8") not in init and anchor2 in init:
            init = init.replace(anchor2, bd_sh + b"\n" + anchor2, 1)
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④c init 调试后门已插入（18777）")

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
                '    # ARM 引擎库路径**只能**挂在 harness 命令上（见下方 exec 行），绝不能 export 进 init 环境：\n'
                '    # 本段跑在 artlaunch 之前，一旦进环境，x86 的 artlaunch 就会去 /thunder-arm/system/lib64\n'
                '    # 找 libdl.so → CANNOT LINK EXECUTABLE ... is for EM_AARCH64 (183) instead of EM_X86_64 (62)\n'
                '    # → 桥退出码 1 →「点一次磁力 = 打死整个爬虫桥」（2026-09-30 实测）。\n'
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
                '        LD_LIBRARY_PATH=/data/catclaw/art/lib:/thunder-arm/system/lib64 /qemu-aarch64-static -L /thunder-arm /harness >>/thunder.log 2>&1\n'
                '        echo "[thunder] harness 退出（code=$?），2s 后重启（引擎任务表清空）"\n'
                '        $BB sleep 2\n'
                '      done\n'
                '    ) &\n'
                '    echo "[thunder] harness 监督循环已起（CTRL_PORT=$TP BLK_DEV=${BLK_DEV:-无}，日志 /thunder.log）"\n'
                'fi\n').encode("utf-8")
            anchor = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
            if anchor not in init:
                raise SystemExit("!! init 的 artlaunch 锚点未找到，迅雷段插入失败")
            # nc 参数日志包装：定位 jar FishConfig 周期探测的 10.0.2.2 端口（诊断用）
            nc_wrap = (b"printf '#!/bin/busybox sh\\necho \"NC-ARGS: $@\" > /dev/ttyS0\\n"
                       b"exec /bin/busybox nc \"$@\"\\n' > /bin/nc && chmod +x /bin/nc\n")
            init = init.replace(anchor, nc_wrap + b"\n" + thunder_sh + b"\n"
                + b"export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64\n"
                + anchor, 1)
            # ⚠️ 上面刚用「过滤同名 + 追加 thunder 条目」重建过 merged，索引整体位移，
            # ④ 步算出的 init_i 已失效。直接沿用旧索引会把**迅雷版 init 的文本写进别的条目槽位**
            # （2026-09-30 实测症状：真 /init 停在无迅雷段的旧版、ui_stub.dex 变成 7.5KB 的 init
            #  文本、gb.dex 始终是 pristine 的旧版 —— 这就是「磁力点了没下文」的产物侧根因）。
            # 必须按名字重新定位 init 再写回。
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("   init 迅雷段已插入（qemu-aarch64 转译 harness）")
    else:
        print("④b （无磁力资产，跳过 thunder 注入）")

    # ── B1.1：guest 内 Wayland 合成器（weston headless）──
    # 目的：vendor 的 hwcomposer.waydroid.so 需要一个**可呈现的 Wayland surface**（它不接受"无显示"），
    # 而我们的 guest 没有显示栈。weston 及其 glibc 依赖以 tar.gz 注入到 /b1/weston.tar.gz
    # （见 .zwork/rebuild_gb.cmd），这里只在存在时解包并拉起；tarball 内是绝对路径布局
    # （/usr/bin/weston、/lib/x86_64-linux-gnu/...、/lib64/ld-linux-x86-64.so.2）。
    # 关掉：构建时设 WESTON=0。
    if os.environ.get("WESTON", "1") != "0":
        we_sh = (
            '\n# ── B1.1：weston（无头 Wayland 合成器；给 waydroid HWC 送画面）──\n'
            'if [ -f /b1/weston.tar.gz ]; then\n'
            '    $BB tar xzf /b1/weston.tar.gz -C / >/tmp/weston-untar.log 2>&1 && echo "[weston] 解包完成" || echo "[weston] 解包失败（见 /tmp/weston-untar.log）"\n'
            '     mkdir -p /tmp/wrt &&  chmod 700 /tmp/wrt\n'
            '    export XDG_RUNTIME_DIR=/tmp/wrt\n'
            '    ( /usr/bin/weston --backend=headless --shell=kiosk --socket=wl-0 --width=1280 --height=720 >/tmp/weston.log 2>&1 ) &\n'
            '    $BB sleep 3\n'
            '    if $BB pgrep -f "weston --backend=headless" >/dev/null 2>&1; then echo "[weston] 已启动（socket wl-0）";\n'
            '    else echo "[weston] 未起来，日志前 8 行："; $BB head -8 /tmp/weston.log 2>/dev/null; fi\n'
            'else\n'
            '    echo "[weston] 未注入 /b1/weston.tar.gz（跳过）"\n'
            'fi\n').encode("utf-8")
    # ── B1.1：DRM/virtio-gpu 内核模块（给 gralloc/minigbm 提供 /dev/dri）──
    # 模块与 guest 内核**版本精确匹配**（Debian 6.1.0-50-amd64 = 6.1.176-1，取自
    # snapshot.debian.org 的 linux-image 包）。QEMU 侧同时要加 `-device virtio-gpu-pci`。
    # 依赖链（modinfo 实测）：virtio-gpu ← drm_kms_helper ← drm，另有 drm_shmem_helper / virtio_dma_buf。
    if os.environ.get("B1_MODS", "1") != "0":
        mods_sh = (
            '\n# ── B1.1：virtio-gpu / DRM 模块（→ /dev/dri）──\n'
            'if [ -f /b1/mods.tar.gz ]; then\n'
            '    $BB tar xzf /b1/mods.tar.gz -C /modules/ && echo "[mods] 解包完成"\n'
            '    for m in drm drm_kms_helper drm_shmem_helper cec drm_display_helper virtio_dma_buf virtio-gpu drm_ttm_helper ttm; do\n'
            '        [ -f /modules/$m.ko ] || continue\n'
            '        $BB insmod /modules/$m.ko >/dev/null 2>&1 && echo "[mods] insmod $m 成功" || echo "[mods] insmod $m 失败（可能已内建/已加载）"\n'
            '    done\n'
            '    $BB ls -la /dev/dri 2>&1 | $BB head -5\n'
            'else\n'
            '    echo "[mods] 未注入 /b1/mods.tar.gz（跳过）"\n'
            'fi\n').encode("utf-8")
        if b"B1.1" not in init and b"/modules/virtio-gpu.ko" not in init:
            init = init.replace(anchor, mods_sh + anchor, 1)
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④e init 已插入 DRM 模块加载段（B1.1）")
    # ── B1.1：Android binder 服务栈（servicemanager / hwservicemanager）──
    # 依赖：binder 设备（本脚本前面已按 binderfs 建好）；属性由 proppreload 提供（LD_PRELOAD 继承）。
    if os.environ.get("B1_ASTACK", "1") != "0":
        ast_sh = (
            '\n# ── B1.1：Android binder 服务栈 ──\n'
            'if [ -f /b1/android-stack.tar.gz ]; then\n'
            '    $BB tar xzf /b1/android-stack.tar.gz -C / && echo "[astack] 解包完成"\n'
            '    # mesa 软件 GL（llvmpipe）：这个 SF 版本没有 CPU 渲染后端，RenderEngine 必须有 EGL。\n'
            '    [ -f /b1/mesa-gl.tar.gz ] && $BB tar xzf /b1/mesa-gl.tar.gz -C / && echo "[astack] mesa GL 已解包"\n'
            '    $BB mkdir -p /dev/binderfs\n'
            '    $BB mount -t binder binder /dev/binderfs 2>/dev/null && echo "[astack] binderfs 已挂载"\n'
            '    for b in binder hwbinder vndbinder; do [ -e /dev/binderfs/$b ] && ln -sf /dev/binderfs/$b /dev/$b; done\n'
            '    # binderfs 没挂成（或内核未建默认节点）时的回落：同实例符号链接。\n'
            '    # 严格说 hwbinder 应有独立实例，但我们的服务栈只需要"能把服务注册进注册表"，同实例可用。\n'
            '    [ -e /dev/hwbinder ] || ln -sf /dev/binder /dev/hwbinder\n'
            '    [ -e /dev/vndbinder ] || ln -sf /dev/binder /dev/vndbinder\n'
            '    $BB ls -la /dev/binder /dev/hwbinder /dev/vndbinder 2>&1 | $BB head -6\n'
            '    export LD_LIBRARY_PATH=/system/lib64\n'
            '    ( LD_PRELOAD=/proppreload.so /system/bin/servicemanager >/tmp/sm.log 2>&1 ) &\n'
            '    ( LD_PRELOAD=/proppreload.so /system/bin/hwservicemanager >/tmp/hsm.log 2>&1 ) &\n'
            '    $BB sleep 3\n'
            '    echo "[astack] servicemanager pid=$($BB pidof servicemanager 2>/dev/null)"\n'
            '    echo "[astack] hwservicemanager pid=$($BB pidof hwservicemanager 2>/dev/null)"\n'
            '    # SurfaceFlinger 不在这里起：必须由桥（Java）起 —— 它要先 set 属性\n'
            '    # hwservicemanager.ready / ro.hardware.hwcomposer / debug.renderengine.backend，\n'
            '    # 而 init 脚本没有 setprop（我们的属性服务是 proppreload 假装的）。见 Server.startSurfaceFlinger。\n'
            '    echo "[astack] sm.log:"; $BB head -4 /tmp/sm.log 2>/dev/null\n'
            '    echo "[astack] hsm.log:"; $BB head -4 /tmp/hsm.log 2>/dev/null\n'
            'else\n'
            '    echo "[astack] 未注入 /b1/android-stack.tar.gz（跳过）"\n'
            'fi\n').encode("utf-8")
        if "B1.1：Android binder 服务栈".encode("utf-8") not in init:
            # ⚠ 不能复用 anchor2：它定义在上面「调试后门」分支里，而后门已改 opt-in（默认不执行）
            # ⇒ 复用会 UnboundLocalError（2026-10-01 实测）。这里用独立锚点。
            _a_ast = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
            init = init.replace(_a_ast, ast_sh + _a_ast, 1)
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④f init 已插入 Android 服务栈段（B1.1）")
        anchor3 = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
        if b"weston --backend=headless" not in init and anchor3 in init:
            init = init.replace(anchor3, we_sh + b"\n" + anchor3, 1)
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④d init 已插入 weston 启动段（B1.1）")
    print("⑤ 重打包 ...")
    buf = io.BytesIO()
    write_cpio_newc(merged, buf)
    with gzip.open(OUT, "wb", compresslevel=1) as g:
        g.write(buf.getvalue())
    print("产物:", OUT, round(os.path.getsize(OUT) / 1048576, 1), "MB")
    print("ALL-DONE")

main()
