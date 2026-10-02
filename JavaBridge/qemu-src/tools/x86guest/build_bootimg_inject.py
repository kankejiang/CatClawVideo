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
            '     mkdir -p /run/user/1000 &&  chmod 700 /run/user/1000 && chown 1000:1000 /run/user/1000\n'
            '    export XDG_RUNTIME_DIR=/run/user/1000\n'
            '    $BB grep -q "^system:" /etc/passwd 2>/dev/null || echo "system:x:1000:1000::/:/system/bin/sh" >> /etc/passwd\n'
        '    ( $BB su system -c "XDG_RUNTIME_DIR=/run/user/1000 /usr/bin/weston --backend=headless --shell=kiosk --socket=wayland-0 --width=1280 --height=720 \" >/tmp/weston.log 2>&1 ) &\n'
            '    $BB sleep 3\n'
            '    if $BB pgrep -x weston >/dev/null 2>&1; then echo "[weston] 已启动（socket wl-0）";\n'
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
            '    # 节点默认 0600 root:root：SF/screencap/探针（shell 或非 root）全被 EACCES 挡住\n'
            '    # （gbmprobe 实测 open errno=13，mapper 因此 NO_RESOURCES）⇒ 放开到 666（binder 同款先例）。\n'
            '    $BB chmod 666 /dev/dri/card0 /dev/dri/renderD128 2>/dev/null\n'
            '    $BB ls -la /dev/dri 2>&1 | $BB head -5\n'
            'else\n'
            '    echo "[mods] 未注入 /b1/mods.tar.gz（跳过）"\n'
            'fi\n').encode("utf-8")
        if b"B1.1" not in init and b"/modules/virtio-gpu.ko" not in init:
            init = init.replace(anchor, mods_sh + anchor, 1)
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④e init 已插入 DRM 模块加载段（B1.1）")
    # ── B1.1：binder 驱动要三个**独立**实例（binder / hwbinder / vndbinder）──
    # 实测：guest 内核**没有 binderfs**（mount -t binder 报 ENODEV ⇒ 拿不到 binder-control，
    # 也就无法用 BINDER_CTL_ADD 建节点），只能把 /dev/hwbinder 符号链接到 /dev/binder。
    # 但同实例会让 HIDL 注册失败：
    #   Could not get transport for …IAllocator/default: Status(EX_TRANSACTION_FAILED): 'BAD_TYPE: '
    #   Failed to register graphics IAllocator 4.0 service.
    # 而 classic binder 驱动暴露 `devices=` 模块参数（内核 CONFIG_ANDROID_BINDER_DEVICES 的入口），
    # 用它一次建出三个独立 misc 设备 ⇒ 在 insmod 循环之前显式加载一次。
    if b"devices=binder,hwbinder,vndbinder" not in init:
        # 循环那行形如：for m in virtio_ring virtio … binder_linux; do
        anchor_loop = b"for m in virtio_ring"
        if anchor_loop in init:
            # ⚠ bytes 字面量只能放 ASCII（踩过两次 SyntaxError），中文部分统一 .encode("utf-8")
            pre = ("\n# B1.1: binder 三实例（binderfs 不可用时的正解）\n"
                   "[ -f /modules/binder_linux.ko ] && $BB insmod /modules/binder_linux.ko "
                   "devices=binder,hwbinder,vndbinder && echo '[init] binder: 三实例已建'\n").encode("utf-8")
            init = init.replace(anchor_loop, pre + anchor_loop, 1)
            print("④g init 已插入 binder 三实例加载（devices= 参数）")

    # ── B1.1：Android binder 服务栈（servicemanager / hwservicemanager）──
    # 依赖：binder 设备（本脚本前面已按 binderfs 建好）；属性由 proppreload 提供（LD_PRELOAD 继承）。
    if os.environ.get("B1_ASTACK", "1") != "0":
        ast_sh = (
            '\n# ── B1.1：Android binder 服务栈 ──\n'
            '# astack v9 (2026-10-02)：dri 软链 tar 化/SF+adbd 拉起/verify 自检/tmpfs 扩容瘦身/SF 注册轮询后 screencap\n'
            'if [ -f /b1/android-stack.tar.gz ]; then\n'
            '    # ⚠ 顺序问题（实测）：本段在 init 里排在 weston 段**之前**，而 composer 服务要连 Wayland\n'
            '    #    （日志 "WAYLAND_DISPLAY: wayland-0 / Could not open Wayland display / failed to open\n'
            '    #     wayland connection"）⇒ 这里先引导 weston，并等 socket 就绪再起图形服务。\n'
            '    [ -x /usr/bin/weston ] || { [ -f /b1/weston.tar.gz ] && $BB tar xzf /b1/weston.tar.gz -C /; }\n'
            '    # HWC(hwcomposer.waydroid) 会**降权到 uid 1000(system)** 并把 XDG_RUNTIME_DIR 算成\n'
            '    # /run/user/1000（实测日志 "XDG_RUNTIME_DIR: /run/user/1000"，完全忽略我们的 env）\n'
            '    # ⇒ weston 也必须以 uid 1000 跑、socket 放 /run/user/1000 才能对上。\n'
            '    $BB mkdir -p /run/user/0 /run/user/1000 && $BB chmod 700 /run/user/0 /run/user/1000\n'
            '    $BB chown 1000:1000 /run/user/1000 2>/dev/null\n'
            '    if [ -x /usr/bin/weston ] && ! $BB pgrep -x weston >/dev/null 2>&1; then\n'
            '        # busybox 没有 setuidgid ⇒ 用 su + /etc/passwd 条目（AID_SYSTEM=1000）\n'
            '        $BB grep -q "^system:" /etc/passwd 2>/dev/null || echo "system:x:1000:1000::/:/system/bin/sh" >> /etc/passwd\n'
            '        $BB grep -q "^system:" /etc/group 2>/dev/null || echo "system:x:1000:" >> /etc/group\n'
            '        ( $BB su system -c "XDG_RUNTIME_DIR=/run/user/1000 /usr/bin/weston --backend=headless --shell=kiosk --socket=wayland-0 --width=1280 --height=720 \" >/tmp/weston.log 2>&1 ) &\n'
            '        $BB sleep 3\n'
            '        echo "[astack] weston 已先于图形服务拉起"\n'
            '    fi\n'
            '    for i in 1 2 3 4 5 6 7 8 9 10; do [ -S /run/user/1000/wayland-0 ] && break; $BB sleep 1; done\n'
            '    if [ -S /run/user/1000/wayland-0 ]; then echo "[astack] Wayland socket 已就绪（/run/user/1000/wayland-0）"; else echo "[astack] 警告：wayland-0 未就绪"; fi\n'
            '    # rootfs 是 tmpfs、默认上限 ~1GB，而基础镜像已占 ~800MB——astack 再解 ~250MB\n'
            '    # 的库必然 ENOSPC（实测 100% 满：libcamera_client 覆盖失败、screencap 写 0 字节）。\n'
            '    # 先把 tmpfs 上限扩到 1500M（RAM 共 2.5G），解完包再删 tar 腾回空间。\n'
            '    $BB mount -o remount,size=1500M / 2>/dev/null && echo "[astack] rootfs 已扩到 1500M"\n'
            '    $BB tar xzf /b1/android-stack.tar.gz -C / && echo "[astack] 解包完成"\n'
            '    # 真属性区：bionic 的 LD_PRELOAD 开关(ro.debuggable) 与 HIDL 的 ready 标记都读它；\n'
            '    # 必须在任何服务之前建好（proppreload 的 per-process 表顶不了跨进程约定）。\n'
            '    [ -x /system/bin/propinit ] && /system/bin/propinit 2>&1 | $BB head -8\n'
            '    # 链接器命名空间：Android 的 libEGL.so loader 用**绝对路径** dlopen("/system/lib64/egl/…")，\n'
            '    # 而链接器只允许 permitted.paths 里的绝对路径 ⇒ 不补就被拒，loader 回落成内置\n'
            '    # "Android META-EGL" 空壳（实测 eglQueryString 自述如此），eglChooseConfig 便一个配置都不给，\n'
            '    # SurfaceFlinger 直接 "no suitable EGLConfig found"。hw/ 是 hw_get_module 找 HAL 用的。\n'
            '    if [ -f /linkerconfig/ld.config.txt ] && ! $BB grep -q "lib64/egl" /linkerconfig/ld.config.txt; then\n'
            '        $BB sed -i "s|^namespace.default.permitted.paths = |namespace.default.permitted.paths = /system/lib64/egl:/system/lib64/hw:/vendor/lib64:/vendor/lib64/egl:/vendor/lib64/hw:|" /linkerconfig/ld.config.txt\n'
            '        echo "[astack] ld.config.txt 已补 egl/hw 的 permitted.paths"\n'
            '    fi\n'
            '    # mesa 软件 GL（llvmpipe）：这个 SF 版本没有 CPU 渲染后端，RenderEngine 必须有 EGL。\n'
            '    [ -f /b1/mesa-gl.tar.gz ] && $BB tar xzf /b1/mesa-gl.tar.gz -C / && echo "[astack] mesa GL 已解包"\n'
            '    # ── tmpfs 瘦身（2026-10-02，T1 验收链实测）──\n'
            '    # rootfs（tmpfs，1GB 上限）100% 满 ⇒ astack 解包半途 ENOSPC（libcamera_client 等\n'
            '    # 覆盖失败、screencap 写不出 PNG）。三个大 tar 解完即删，腾回 ~106MB。\n'
            '    $BB rm -f /b1/android-stack.tar.gz /b1/mesa-gl.tar.gz /b1/weston.tar.gz\n'
            '    $BB chmod 644 /system/lib64/*.so 2>/dev/null\n'
            '    echo "[astack] tmpfs 瘦身: $($BB df -h / 2>/dev/null | $BB tail -1)"\n'
            '    # mesa 的 DRI 搜索路径：dri/<driver>_dri.so 必须软链到 ../libgallium_dri.so\n'
            '    # （108 的 Waydroid 容器就是 init 建的；缺了它 gbm_create_device 直接 ENOENT => mapper NO_RESOURCES）\n'
            '    $BB mkdir -p /system/lib64/dri /vendor/lib64/dri 2>/dev/null\n'
            '    for _n in swrast llvmpipe iris virtio_gpu kms_swrast zink; do\n'
            '        $BB ln -sf /system/lib64/libgallium_dri.so /system/lib64/dri/${_n}_dri.so 2>/dev/null\n'
            '        $BB ln -sf /system/lib64/libgallium_dri.so /vendor/lib64/dri/${_n}_dri.so 2>/dev/null\n'
            '    done\n'
            '    $BB cp -f /system/lib64/libgallium_dri.so /vendor/lib64/libgallium_dri.so 2>/dev/null\n'
            '    # minigbm 的后端插件搜索路径是 /vendor/lib64/：先试 <gpu>_gbm.so 再回退 dri_gbm.so\n'
            '    # （108 Waydroid 参照系实锤：/vendor/lib64/ 同时有 dri_gbm.so + libgallium_dri.so，\n'
            '    #   且不存在 virtio_gpu_gbm.so ⇒ 走的就是 dri_gbm.so 回退）——缺了它 gbm_create_device=ENOENT。\n'
            '    $BB cp -f /system/lib64/dri_gbm.so /vendor/lib64/dri_gbm.so 2>/dev/null\n'
            '    # screencap 落盘目录：shell(uid 2000) 写不了 /data 根，建 777 目录给它\n'
            '    # （/data 是启动时重挂的 tmpfs，镜像树里 mk_win 建的 data/local/tmp 不会存活）\n'
            '    $BB mkdir -p /data/local/tmp\n'
    '    $BB chmod 777 /data/local/tmp\n'
    '    # dalvik-cache 同理会被 /data tmpfs 重挂清掉；app_process 起 ART 时要往\n'
    '    # /data/dalvik-cache/x86_64 写缓存，目录缺失 = 启动即 SIGABRT（2026-10-02 实测）\n'
    '    $BB mkdir -p /data/dalvik-cache/x86_64\n'
            '    echo "[astack] dri 目录: $($BB ls /system/lib64/dri 2>/dev/null | $BB tr \\"\\n\\" \\" \\")"\n'
            '    $BB mkdir -p /dev/binderfs\n'
            '    # binderfs 的节点必须用 ioctl(BINDER_CTL_ADD) 创建（挂载本身只给 binder-control）。\n'
            '    # 为什么必须独立实例：HIDL 注册走 /dev/hwbinder，符号链接到 /dev/binder（同实例）时实测报\n'
            '    #   Could not get transport for ...IAllocator/default: Status(EX_TRANSACTION_FAILED): BAD_TYPE\n'
            '    [ -x /system/bin/mkbinders ] && /system/bin/mkbinders 2>&1 | $BB head -8\n'
            '    for b in binder hwbinder vndbinder; do [ -e /dev/binderfs/$b ] && ln -sf /dev/binderfs/$b /dev/$b; done\n'
            '    # binderfs 没挂成（或内核未建默认节点）时的回落：同实例符号链接。\n'
            '    # 严格说 hwbinder 应有独立实例，但至少能让 servicemanager 起来。\n'
            '    [ -e /dev/hwbinder ] || ln -sf /dev/binder /dev/hwbinder\n'
            '    [ -e /dev/vndbinder ] || ln -sf /dev/binder /dev/vndbinder\n'
            '    $BB ls -la /dev/binder /dev/hwbinder /dev/vndbinder 2>&1 | $BB head -6\n'
            '    # devpts：adb shell / exec-out 需要可用的 pty（实测 screencap 报\n'
            '    # "failed to create pty master" 就是这个原因，与显示栈无关）。\n'
            '    $BB mkdir -p /dev/pts\n'
            '    [ -c /dev/ptmx ] || $BB mknod /dev/ptmx c 5 2 2>/dev/null\n'
            '    $BB chmod 666 /dev/ptmx 2>/dev/null\n'
            '    $BB mount -t devpts devpts /dev/pts 2>/dev/null && echo "[astack] devpts 已挂载"\n'
            '    # binder 节点默认 0600 root ⇒ adb shell(uid=shell) 下跑 screencap 会报\n'
            '    # "Binder driver /dev/binder could not be opened: Permission denied"（实测）⇒ 放开权限。\n'
            '    $BB chmod 666 /dev/binder /dev/hwbinder /dev/vndbinder 2>/dev/null\n'
            '    # 与 108 对齐：vndservicemanager 服务 /dev/vndbinder 上的 vendor HAL（108 在跑）\n'
            '    VNDSM=/vendor/bin/vndservicemanager; [ -x $VNDSM ] || VNDSM=/vendor/bin/hw/vndservicemanager; if [ -x $VNDSM ]; then\n'
            '        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true" $VNDSM /dev/vndbinder >/tmp/vndsm.log 2>&1 ) &\n'
            '        $BB sleep 1\n'
            '        echo "[astack] vndservicemanager pid=$($BB pidof vndservicemanager 2>/dev/null)"\n'
            '    fi\n'
            '    export LD_LIBRARY_PATH=/system/lib64\n'
            '    ( LD_PRELOAD=/proppreload.so /system/bin/servicemanager >/tmp/sm.log 2>&1 ) &\n'
            '    ( LD_PRELOAD=/proppreload.so /system/bin/hwservicemanager >/tmp/hsm.log 2>&1 ) &\n'
            '    $BB sleep 2\n'
            '    # 图形 HAL 服务：mesa 的 Android EGL 平台初始化需要 mapper@4.0 服务（由 allocator@4.0 提供），\n'
            '    # SF 需要 composer。rc 里它们的启动条件是 ro.hardware.gralloc=minigbm_gbm_mesa（由 PROPFIX 提供）。\n'
            '    # configstore：SF 要 configstore@1.0::ISurfaceFlingerConfigs；不注册它会一直\n'
            '    # 打 "trying to start it as a lazy HAL"（我们没跑 init，没有 lazy HAL 机制）⇒ 必须自己起。\n'
            '    # 启动顺序：allocator(mapper) 必须先于 composer —— DRM master 唯一，谁先 open /dev/dri/card0\n'
            '    # 谁是 master；mapper 的 wrapper 要求 master（实测非 master 时 gbm_create_device 返回 EINVAL），\n'
            '    # composer 晚起也能工作（weston headless 不占 DRM master）。\n'
            '    for svc in android.hardware.graphics.allocator@4.0-service.minigbm_gbm_mesa android.hardware.configstore@1.1-service android.hardware.graphics.composer@2.1-service; do\n'
            '        [ -x /vendor/bin/hw/$svc ] || continue\n'
            # 后端修复（2026-10-02）：wrapper 的后端链 virtio_gpu_gbm.so（缺）→ dri_gbm.so（哑元+SEGV）
    # ⇒ inject 层把 libgbm_mesa.so 补到 virtio_gpu_gbm.so 槽位（同 gbm ABI，mesa 自动选
    #    kms_swrast 走 DRM dumb 真分配——gbmprobe 直连 libgbm_mesa 实测真分配成功）。
    '        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX_CRASH=1 PROPFIX_DEBUG=1 PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0;persist.waydroid.width=1280;persist.waydroid.height=720;persist.waydroid.use_subsurface=false;persist.waydroid.multi_windows=false;persist.waydroid.no_presentation=true;persist.waydroid.no_background_subsurface=true;persist.waydroid.cursor_on_subsurface=false;persist.waydroid.cursor_force_shm=true;persist.waydroid.reverse_scrolling=false;persist.waydroid.width_padding=0;persist.waydroid.height_padding=0" LD_LIBRARY_PATH=/system/lib64/egl:/system/lib64/hw:/vendor/lib64/egl:/vendor/lib64/hw:/vendor/lib64:/system/lib64 WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 /vendor/bin/hw/$svc >/tmp/$svc.log 2>&1 ) &\n'
            '        echo "[astack] 已拉起 $svc"\n'
            '    done\n'
            '    # Waydroid 专有 task 服务（system 侧）：SF/HWC 会等它\n'
            '    for svc in vendor.waydroid.task@1.0-service; do\n'
            '        [ -x /system/bin/hw/$svc ] || continue\n'
            '        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0" LD_LIBRARY_PATH=/system/lib64/egl:/system/lib64/hw:/vendor/lib64:/system/lib64 /system/bin/hw/$svc >/tmp/$svc.log 2>&1 ) &\n'
            '        echo "[astack] 已拉起 $svc"\n'
            '    done\n'
            '    $BB sleep 4\n'
            '    # 启动顺序：allocator(mapper) 必须先于 composer —— DRM master 唯一，谁先 open /dev/dri/card0\n'
            '    # 谁是 master；mapper 的 wrapper 要求 master（实测非 master 时 gbm_create_device 返回 EINVAL），\n'
            '    # composer 晚起也能工作（weston headless 不占 DRM master）。\n'
            '    for svc in android.hardware.graphics.allocator@4.0-service.minigbm_gbm_mesa android.hardware.configstore@1.1-service android.hardware.graphics.composer@2.1-service; do\n'
            '        echo "[astack] $svc 日志:"; $BB cat /tmp/$svc.log | $BB head -8\n'
            '    done\n'
            '    echo "[astack] hwservicemanager 存活: pid=$($BB pidof hwservicemanager)"\n'
            '    # EGL 隔离探针：SF 里 EGL 的失败信息被 liblog 吞掉（乱码），这里用独立进程走同一条 EGL 路径，\n'
            '    # mesa 的 stderr 与每一步的 errno 都直接进控制台。跑完就退出，不影响后续。\n'
            '    if [ -x /system/bin/eglprobe ]; then\n'
            '        $BB sleep 2\n'
            '        LD_PRELOAD=/system/lib64/libLLVM22.so:/system/lib64/libpropfix.so:/proppreload.so \\\n'
            '        PROPFIX="hwservicemanager.ready=true;ro.hardware.egl=angle;ro.hardware.vulkan=lvp;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0" \\\n'
            '        GALLIUM_DRIVER=swrast LIBGL_ALWAYS_SOFTWARE=1 MESA_LOADER_DRIVER_OVERRIDE=swrast \\\n'
            '        LD_LIBRARY_PATH=/vendor/lib64/egl:/vendor/lib64:/system/lib64:/system/lib64/egl \\\n'
            '        /system/bin/eglprobe 2>&1 | $BB head -20\n'
            '    fi\n'
            '    # GBM 探针：独立复现 minigbm/mesa 的分配，绕开符号拦截拿真实错误\n'
            '    if [ -x /system/bin/gbmprobe ]; then\n'
            '        LD_LIBRARY_PATH=/system/lib64:/vendor/lib64 LD_PRELOAD=/system/lib64/libpropfix.so PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;gralloc.gbm.device=/dev/dri/card0" /system/bin/gbmprobe 2>&1 | $BB head -22\n'
            '    fi\n'            '    # 服务清单检查：AIDL 的 SurfaceFlingerAIDL 是否注册（screencap 要靠它）\n'
            '    if [ -x /system/bin/svccheck ]; then\n'
            '        # 后台周期查询：SF 由桥(Java)在 init 之后才拉起，单次早查无意义\n'
            '        ( for i in 1 2 3 4 5 6; do\n'
            '            $BB sleep 30\n'
            '            echo "[svccheck] 第 $i 次（约 $((i*30)) 秒）："\n'
            '            LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true" /system/bin/svccheck 2>&1 | $BB grep -E "SurfaceFlinger|结束"\n'
            '          done ) &\n'
            '    fi\n'
            '    # HAL 服务注册完成的哨兵：桥（Java）会等它再拉起 SurfaceFlinger。\n'
            '    # 实测 SF 与 HAL 注册存在竞争：抢跑会撞上间歇性的 gralloc-mapper is missing / libEGL 找不到实现。\n'
            '    $BB sleep 2\n'
            '    $BB touch /tmp/hal_ready\n'
            '    echo "[astack] HAL ready sentinel written to /tmp/hal_ready"\n'
            '    # ── SF/adbd 由 init 直接拉起（2026-10-02）──\n'
            '    # 桥内 ProcessBuilder 的 fork 被 proppreload 拦截（EAGAIN，实测 [adbd]/[sf]\n'
            '    # "Cannot run program ... error=11"）⇒ 移到 init：init 的 sh 不挂 proppreload。\n'
            '    # 桥侧拉起保留（撞 HAL 注册名起不来，无害）。环境照抄 Server.startSF，\n'
            '    # 但 gralloc.gbm.device 用 card0（renderD 分配 EACCES，card0 实测成功）。\n'
            '    # SF 环境与 allocator 同源：gralloc 走 allocator 服务（HAL），SF 自身 EGL=ANGLE(Vulkan lvp)，\n'
    '    # GALLIUM_* 不影响它 —— 保留原始 swrast 值仅为对齐 Server.startSF 的历史行为。\n'
    '    ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX_CRASH=1 PROPFIX_DEBUG=1 \\\n'
    '      PROPFIX="hwservicemanager.ready=true;ro.hardware.hwcomposer=waydroid;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.egl=angle;ro.hardware.vulkan=lvp;gralloc.gbm.device=/dev/dri/card0;debug.renderengine.backend=skiagl;ro.surface_flinger.has_wide_color_display=false;ro.surface_flinger.has_HDR_display=false;ro.surface_flinger.use_color_management=false" \\\n'
    '      GALLIUM_DRIVER=swrast MESA_LOADER_DRIVER_OVERRIDE=swrast LIBGL_ALWAYS_SOFTWARE=1 \\\n'
            '      LD_LIBRARY_PATH=/vendor/lib64/egl:/vendor/lib64:/system/lib64:/system/lib64/egl \\\n'
            '      WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/0 /system/bin/surfaceflinger >/tmp/sf-init.log 2>&1 ) &\n'
            '    # adbd 必须挂 proppreload（PROPFIX 提供 service.adb.tcp.port 等启动属性），\n'
            '    # 而 proppreload 的 fork 拦截会弄死 adbd 的 shell 子进程（实测 "fork failed:\n'
            '    # Try again"）——已在 proppreload.c 按 cmdline 白名单放行 adbd（2026-10-02）。\n'
            '    # E2 模式曾试过 setprop 走真 init 的 rc adbd：实测 setprop rc=1、rc 服务环\n'
            '    # 根本没起来（init.svc=0）⇒ 此路不通，两种模式统一自己拉。\n'
            '    ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so \\\n'
            '      PROPFIX="hwservicemanager.ready=true;service.adb.tcp.port=5555;service.adb.root=1;ro.adb.secure=0;ro.debuggable=1;persist.adb.tls_server.enable=0" \\\n'
            '      LD_LIBRARY_PATH=/vendor/lib64:/system/lib64 /system/bin/adbd >/tmp/adbd-init.log 2>&1 ) &\n'
                    '    echo "[astack] SF/adbd 已由 init 拉起"\n'
            '    # 1 秒同步探针：区分「子 shell 没跑/重定向失败」与「进程秒死但日志在」——\n'
            '    # 若 1 秒后文件不存在，说明 spawn 或重定向本身失败（上一轮 sf-init.log 消失的判别点）。\n'
            '    $BB sleep 1\n'
            '    echo "[astack] 启动1秒探针 sf/adbd 日志:"; $BB ls -la /tmp/sf-init.log /tmp/adbd-init.log 2>&1\n'
            '    # ── 验收自检（无需 adb）：20 秒全量证据 + 35 秒 screencap 出 PNG 魔数 ──\n'
            '    ( $BB sleep 20; \\\n'
            '      echo "[verify] ===== /tmp 清单 ====="; $BB ls -la /tmp/ 2>&1 | $BB head -24; \\\n'
            '      echo "[verify] ===== 进程清单 ====="; ($BB ps w 2>/dev/null || $BB ps) 2>&1 | $BB grep -E "surfaceflinger|adbd|servicemanager|hwservicemanager|weston|allocator|composer|vndservice" | $BB grep -v grep; \\\n'
            '      echo "[verify] ===== dri 目录 ====="; $BB ls -la /system/lib64/dri/ /vendor/lib64/dri/ 2>&1 | $BB head -24; \\\n'
            '      echo "[verify] ===== 关键文件 ====="; $BB ls -la /system/bin/surfaceflinger /system/bin/adbd /system/bin/screencap /system/bin/svccheck /dev/dri/card0 /dev/dri/renderD128 2>&1; \\\n'
            '      echo "[verify] ===== 内存 ====="; $BB head -2 /proc/meminfo; $BB dmesg 2>/dev/null | $BB tail -6; \\\n'
            '      echo "[verify] ===== sf-init.log 关键行 ====="; $BB grep -aE "allocate|RenderEngine|EGL|eglCreate|dispatcher|AIDL|VINTF|surfaceflinger" /tmp/sf-init.log 2>/dev/null | $BB head -30; echo "[verify] ---- sf-init.log 尾部 ----"; $BB tail -12 /tmp/sf-init.log 2>/dev/null; \\\n'
            '      echo "[verify] ===== adbd-init.log ====="; if [ -f /tmp/adbd-init.log ]; then $BB head -15 /tmp/adbd-init.log; else echo "(不存在)"; fi ) &\n'
            '    ( $BB sleep 30; \\\n'
            '      # screencap 会卡死在 getService 的无限等待（v8 实测 rc=143 三连）——\n'
            '      # 必须先轮询到 SF 服务注册完成再跑。SF 注册在部分 boot 上晚于 +30s。\n'
            '      _OK=0; _N=0; \\\n'
            '      for _i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30; do \\\n'
            '        _N=$_i; \\\n'
            '        if LD_LIBRARY_PATH=/system/lib64 service check SurfaceFlinger 2>/dev/null | $BB grep -q "Service found"; then _OK=1; break; fi; \\\n'
            '        $BB sleep 6; \\\n'
            '      done; \\\n'
            '      echo "[verify] SF 轮询 found=$_OK 第${_N}次"; \\\n'
            '      if [ "$_OK" = 1 ]; then \\\n'
            '        _SC=$(LD_LIBRARY_PATH=/system/lib64 timeout 30 /system/bin/screencap -p /data/local/tmp/shot.png 2>&1); _RC=$?; \\\n'
            '        echo "[verify] SCREENCAP rc=$_RC err=[$_SC] size=$($BB wc -c < /data/local/tmp/shot.png 2>/dev/null) png8=$($BB od -An -tx1 -N8 /data/local/tmp/shot.png 2>/dev/null | $BB tr -d \" \\n\")"; \\\n'
            '      fi ) &\n'
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
        # ── astack 段幂等更新（2026-10-02）：原守卫「标记不存在才插入」导致基础 initramfs 里
        # 固化的**旧版** astack 段永远不被更新 ⇒ dri 软链/SF/adbd 拉起/verify 等所有修改从未生效。
        # 改为版本标记：ver 不在（=旧段在）时先整段删除旧段（从标记到段尾 fi），再插新版。
        _ver = b"# astack v9 (2026-10-02)"
        if _ver not in init:
            tag = "B1.1：Android binder 服务栈".encode("utf-8")
            start = init.find(tag)
            if start >= 0:
                endmark = 'echo "[astack] 未注入 /b1/android-stack.tar.gz（跳过）"\nfi\n'.encode("utf-8")
                end = init.find(endmark, start)
                if end >= 0:
                    init = init[:start] + init[end + len(endmark):]
                    print("④e init 已删除旧版 astack 段")
            _a_ast = b'LD_PRELOAD=/proppreload.so /system/bin/artlaunch'
            init = init.replace(_a_ast, ast_sh + _a_ast, 1)
            init_i = next(i for i, e in enumerate(merged) if e[0] == "init")
            merged[init_i] = merged[init_i][:6] + (init,) + merged[init_i][7:]
            print("④f init 已插入 Android 服务栈段（astack v9，含 SF 注册轮询 + screencap 自检）")
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
