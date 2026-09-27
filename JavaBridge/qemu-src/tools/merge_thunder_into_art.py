#!/usr/bin/env python3
"""合并迅雷引擎到 ART guest（aarch64）：一个 VM 同时跑「爬虫桥 + 迅雷引擎」。

背景（2026-09-27）：现状是两个独立 QEMU guest——
  · ART guest（art_initrd.gz，QemuArtGuest 起）：爬虫桥 + Guard 壳；
  · 迅雷 guest（pkg_initrd.gz 4.7MB，QemuGuestEngine 起）：harness + libxl_thunder_sdk。
两者同架构（aarch64）、同内核（pkg_kernel）、同 bionic 用户态——可共用一 VM：
  · /harness 与 linker64 已在 ART initrd（早期一并放入）；
  · 并入 libxl_thunder_sdk.so / libxl_stat.so / thunder-data 预置；
  · init 追加段：网络配好后（桥起之前）拉起 harness（回连宿主 CTRL_PORT）。

宿主侧配套（另做）：QemuGuestEngine 改「外部 VM 模式」（不再自起 QEMU，租用 ART VM 的
数据盘 /dev/vda + swap + 媒体口 hostfwd；控制口 18080 由 guest 主动回连，天然可用）。

用法：python merge_thunder_into_art.py <art_initrd.gz> <pkg_initrd.gz> <out.gz>
"""
import gzip
import io
import os
import sys

# 从 pkg（迅雷）并入 ART 的文件（ART 里没有的才追加；已有同名则替换）
# ⚠ harness 必须从 pkg 取：pkg_initrd.gz 由 repack_initrd.cs 维护（ctrlloop.c 改动后
#   重编译的 harness 都打进那里），ART 基座里是旧版 —— 追加条目在 cpio 末尾、解包时
#   覆盖同名前条目，pkg 的新 harness 才是生效的那份（2026-09-27 实测漏加导致
#   「EXIT 命令不生效」假象）。
TAKE_FROM_PKG = {
    "harness",
    "system/lib64/libxl_thunder_sdk.so",
    "system/lib64/libxl_stat.so",
    "thunder-data/setting.cfg",
    "thunder-data/Identify2.txt",
}

# init 追加段：插在「网卡/DNS 配好之后」。位置锚点 = ART init 里的 hello 横幅行。
ANCHOR = 'echo "=== CatClaw ART guest begin (bridgeport=$PORT) ==="'

THUNDER_SEG = '''
# ── 迅雷引擎段（合并自 pkg guest，2026-09-27）───────────────────────────────
# 与爬虫桥同 guest：引擎回连宿主 10.0.2.2:$CTRL（cmdline thunderport= 传），
# 数据面块设备按 cmdline blkdev=（宿主挂盘；没有则 harness 自行回退纯转发）。
# 任务下发走控制口协议（同 QemuGuestEngine 的既有命令），本段只保证进程常驻。
TP=$(getarg thunderport)
if [ -n "$TP" ] && [ -x /harness ]; then
    # ── 环境：pkg init 烧死的那组 export，这里必须补齐 ──
    # CTRL_HOST：缺省 127.0.0.1 = guest 自己（本机直跑调试用）；guest 里必须指向宿主
    #（SLIRP 的 10.0.2.2）。漏掉它 harness 一直回连 guest 本机——控制端永远收不到轮询。
    export CTRL_HOST="10.0.2.2"
    export CTRL_PORT="$TP"
    export PROXY_PORT="20080"          # guest 代理监听口：宿主 media hostfwd（-:20080）对准它
    export P2SP_SECS="0" DL_SECS="0"   # 与 pkg init 一致（0 = 不限时）
    # 数据面块设备：跟 pkg init 一样从 cmdline 取，**绝不硬编码 /dev/vda**——
    # swap 盘先挂而数据盘缺位时 vda 会是 swap 盘（引擎字节写进交换区）。
    export BLK_DEV="$(getarg blkdev)"
    export QCO="${QCO:-1}"
    # 交换区（cmdline swapdev=，raw 设备）：让内存盘冷页换出到宿主盘（写法对齐 pkg init）
    SD=$(getarg swapdev)
    if [ -n "$SD" ] && [ -b "$SD" ]; then
        $BB mkswap "$SD" 2>/dev/null
        $BB swapon "$SD" 2>/dev/null && echo "[thunder] swap on $SD"
    fi
    # 本段插在「ART guest begin」横幅后，此刻 eth0 可能还没配（ART init 的 ifconfig 在后）。
    # harness 的轮询失败本身会自愈（main_loop 无限重试），但日志会刷屏 —— 最多等 30s 等网起来。
    i=0
    while [ $i -lt 30 ] && ! $BB ifconfig eth0 2>/dev/null | $BB grep -q 10.0.2.15; do
        $BB sleep 1; i=$((i+1))
    done
    $BB mkdir -p /thunder-data 2>/dev/null
    # 监督循环：harness 退出（崩溃 / 宿主 EXIT 重置）→ 2s 后拉起新进程。
    # 引擎任务表随进程清空（合并模式宿主靠它做 9128 等价「重启 VM」复位）；
    # /thunder-data 是 VM 级 tmpfs、块设备数据在宿主镜像 —— 都不随 harness 进程死。
    # 输出续写 /thunder.log（不吞）：排障要看崩溃/连接日志（cat /thunder.log）。
    (
      while true; do
        /harness >>/thunder.log 2>&1
        echo "[thunder] harness 退出（code=$?），2s 后重启（引擎任务表清空）"
        $BB sleep 2
      done
    ) &
    echo "[thunder] harness 监督循环已起（CTRL_PORT=$TP BLK_DEV=${BLK_DEV:-无}，日志 /thunder.log）"
fi
'''


def read_newc(f):
    """读一条 newc 条目 → (hdr_bytes, name, data) 或 None(EOF)。"""
    hdr = f.read(110)
    if len(hdr) < 110:
        return None
    if hdr[:6] != b"070701":
        raise SystemExit("非 newc header: %r" % hdr[:16])
    filesize = int(hdr[54:62], 16)
    namesize = int(hdr[94:102], 16)
    name = f.read(namesize).rstrip(b"\0").decode("utf-8", "replace")
    pad = (-(110 + namesize)) % 4
    if pad:
        f.read(pad)
    if name == "TRAILER!!!":
        return (hdr, name, b"")
    data = f.read(filesize)
    pad2 = (-filesize) % 4
    if pad2:
        f.read(pad2)
    return (hdr, name, data)


def write_newc(o, hdr, name, data):
    h = bytearray(hdr)
    nb = name.encode()
    h[54:62] = ("%08X" % len(data)).encode()
    # ⚠ namesize（含 NUL）必须按「实际写入的名字」重写：追加条目用的是 pkg 的原 header，
    #   若 pkg 原名与这里的 "./"+key 长度不同，不重写该字段读回立即错位——2026-09-27 实锤：
    #   旧版合并 initrd 解包到追加条目即 garbage（"非 newc header"），108 冒烟起不来。
    h[94:102] = ("%08X" % (len(nb) + 1)).encode()
    o.write(bytes(h))
    o.write(nb + b"\0")
    pad = (-(110 + len(nb) + 1)) % 4
    if pad:
        o.write(b"\0" * pad)
    o.write(data)
    if (-len(data)) % 4:
        o.write(b"\0" * ((-len(data)) % 4))


def trailer_bytes():
    name = b"TRAILER!!!"
    hdr = bytearray(b"070701")
    hdr += b"0" * 104
    hdr[94:102] = ("%08X" % (len(name) + 1)).encode()
    return bytes(hdr) + name + b"\0" + b"\0" * ((-(110 + len(name) + 1)) % 4)


ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"


def read_initrd_f(path):
    """initrd 读取：按 magic 自动识别 zstd / gzip（art 基座自 2026-09-28 起为 zstd-19）。"""
    with open(path, "rb") as f:
        magic = f.read(4)
    if magic == ZSTD_MAGIC:
        import zstandard
        with open(path, "rb") as f:
            return io.BytesIO(zstandard.ZstdDecompressor().stream_reader(f).read())
    return gzip.open(path, "rb")


def write_initrd_f(path, raw):
    """initrd 写入：zstd -19（比 gzip -6 小 ~25%，解压快 3~5 倍；Alpine 6.12 内核
    CONFIG_RD_ZSTD=y，按 magic 自动识别）。无 zstandard 库时回退 gzip。"""
    try:
        import zstandard
        blob = zstandard.ZstdCompressor(level=19).compress(raw)
        kind = "zstd-19"
    except ImportError:
        blob = gzip.compress(raw, 6, mtime=0)
        kind = "gzip-6"
    with open(path, "wb") as f:
        f.write(blob)
    print("输出压缩: %s（%.1fMB）" % (kind, len(blob) / 1048576))


def main():
    art_path, pkg_path, out_path = sys.argv[1], sys.argv[2], sys.argv[3]

    # 1) 读 pkg，提取迅雷资产
    take = {}
    with gzip.open(pkg_path, "rb") as f:
        while True:
            e = read_newc(f)
            if e is None:
                break
            hdr, name, data = e
            if name == "TRAILER!!!":
                break
            key = name.lstrip("./")
            if key in TAKE_FROM_PKG:
                take[key] = (hdr, data)
    missing = TAKE_FROM_PKG - set(take)
    if missing:
        raise SystemExit("pkg 里缺文件: %s" % ", ".join(sorted(missing)))
    print("从 pkg 提取: %s" % ", ".join("%s(%dB)" % (k, len(v[1])) for k, v in sorted(take.items())))

    replaced = set()
    appended = set()
    buf = io.BytesIO()
    with read_initrd_f(art_path) as f:
        # 2) 全量转写 ART，替换 init
        while True:
            e = read_newc(f)
            if e is None:
                break
            hdr, name, data = e
            if name == "TRAILER!!!":
                break
            key = name.lstrip("./")
            if key == "init":
                txt = data.decode("utf-8", "replace")
                if ANCHOR not in txt:
                    raise SystemExit("init 里找不到锚点（ART init 已变化，检查后更新脚本）")
                txt = txt.replace(ANCHOR, ANCHOR + THUNDER_SEG)
                data = txt.encode("utf-8")
                print("替换 init（+%d 字节迅雷段）" % (len(THUNDER_SEG)))
                replaced.add("init")
            write_newc(buf, hdr, name, data)

        # 3) 追加迅雷资产（用 pkg 的原 header，改名为 ./<key>）
        for key, (hdr, data) in sorted(take.items()):
            write_newc(buf, hdr, "./" + key, data)
            appended.add(key)
            print("追加 %s（%dB）" % (key, len(data)))

        buf.write(trailer_bytes())

    write_initrd_f(out_path, buf.getvalue())

    print("完成：%s（%d 条替换, %d 条追加）" % (out_path, len(replaced), len(appended)))


if __name__ == "__main__":
    main()
