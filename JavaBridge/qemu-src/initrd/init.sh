#!/bin/busybox sh
BB=/bin/busybox
export PATH=/bin
$BB mkdir -p /proc /sys /dev /etc /tmp
$BB mount -t proc     proc /proc 2>/dev/null
$BB mount -t sysfs    sys  /sys  2>/dev/null
$BB mount -t devtmpfs dev  /dev  2>/dev/null

# ═══ 宿主经内核 cmdline 传入的运行参数（缺省时与旧行为完全一致）═══
#   swapdev=/dev/vdX  宿主稀疏镜像当交换区（**raw 设备，不需要文件系统**）
#   tdata=<N>[mMgG]   /thunder-data 的 tmpfs 大小
#   blkdev=/dev/vdX   数据面块设备（引擎字节按偏移直写，宿主直读同一物理文件）
# ★ 为什么要有 swap：/thunder-data 是 tmpfs，其页**可以换出**（ramfs 不行）。
#   给一块宿主盘当 swap，就能把已完成/离播放头远的冷数据换到宿主磁盘上 ——
#   guest RAM 不再随下载量线性增长（原先 -m 必须给到 5120 才装得下 3500m 的内存盘）。
CMDLINE=$($BB cat /proc/cmdline)
getarg() {
  for a in $CMDLINE; do
    case "$a" in
      "$1"=*) echo "${a#*=}"; return ;;
    esac
  done
}
SWAPDEV=$(getarg swapdev)
BLKDEV=$(getarg blkdev)
TDATA=$(getarg tdata)
[ -z "$TDATA" ] && TDATA=3500m
echo "=== [init] cmdline 参数：swapdev=${SWAPDEV:-（无）} tdata=$TDATA blkdev=${BLKDEV:-（无）} ==="

echo "=== [init] 内核模块 ==="
# virtio_blk：既是数据面块设备，也是交换区载体（两者都由宿主用 -drive 挂上）。
# 不是所有环境都会挂这些盘 —— insmod 失败不致命，harness 侧 blk_open() 会自行回退。
for m in failover net_failover virtio_net virtio_blk; do
  if $BB insmod /modules/$m.ko 2>&1; then echo "  insmod $m ... ok"; else echo "  insmod $m ... 失败（不致命）"; fi
done
$BB sleep 1   # virtio-blk 探测是异步的：等 /dev/vdX 出现，否则下面 -b 判断与 swapon 都会落空

# ═══ 交换区：让 tmpfs 的冷页换出到宿主盘 ═══
SWAPOK=0
if [ -n "$SWAPDEV" ]; then
  if [ -b "$SWAPDEV" ]; then
    echo "=== [init] 启用交换区 $SWAPDEV ==="
    # ⚠ busybox 的 mkswap **不支持 -f**（传了会只打 usage 而不写签名，随后内核报
    #   「Unable to find swap-space signature」）。直接调，不带参数。
    $BB mkswap "$SWAPDEV" 2>&1 | $BB tail -3
    if $BB swapon "$SWAPDEV" 2>&1; then
      SWAPOK=1
      echo "  swapon ok"
    else
      echo "  ⚠ swapon 失败 → 退化为纯内存 tmpfs"
    fi
    # ⚠ busybox swapon 也没有 -s（从 /proc/swaps 读才是可移植写法）
    $BB cat /proc/swaps 2>&1
  else
    echo "=== [init] ⚠ swapdev=$SWAPDEV 不是块设备（宿主没挂上？）→ 跳过交换区 ==="
  fi
else
  echo "=== [init] 未提供交换区 → 纯内存 tmpfs ==="
fi

# ★ 没有可换出空间时，tmpfs 必须真的装得进 RAM：按 MemTotal 留 1GB 给内核/initrd/引擎，
#   否则引擎一写满就往 OOM 走（比旧行为更糟）。这是安全兜底，不是常规路径。
if [ "$SWAPOK" = "0" ]; then
  TOTAL=$($BB awk '/^MemTotal:/{print int($2/1024)}' /proc/meminfo)
  SAFE=$((TOTAL - 1024))
  [ "$SAFE" -lt 768 ] && SAFE=768
  TDATA="${SAFE}m"
  echo "  ⚠ 无 swap：tmpfs 收敛为 $TDATA（MemTotal ${TOTAL}MB − 1GB 余量），避免 guest OOM"
fi

# ★ /thunder-data 必须挂**真 tmpfs**：initramfs 的 rootfs 是 ramfs，
#   而 ramfs 的 statvfs 空闲空间上报不可信（通常 0）—— 迅雷（尤其 BT 任务要预分配整个文件）
#   很可能据此判定「磁盘空间不足」，然后**静默不下数据**（任务一直 st=1、零字节）。
$BB mkdir -p /preload
$BB cp -r /thunder-data/. /preload/ 2>/dev/null
$BB mount -t tmpfs -o size=$TDATA tmpfs /thunder-data
$BB cp -r /preload/. /thunder-data/ 2>/dev/null
echo "=== [init] /thunder-data 已挂 tmpfs（$TDATA，冷页可换出到 swap）==="
$BB df -h /thunder-data
$BB free 2>/dev/null
echo "=== [init] 引导完成 ==="

echo "=== [init] 网卡配置 ==="
$BB ip link set eth0 up 2>&1
$BB ip link set lo up 2>&1
$BB ip addr add 127.0.0.1/8 dev lo 2>&1
$BB ip addr add 10.0.2.15/24 dev eth0 2>&1
$BB ip route add default via 10.0.2.2 2>&1
echo "nameserver 10.0.2.3" > /etc/resolv.conf
# ★ hosts 劫持实验（v2）：迅雷已把 btrouter/master.wap.dphub 下线成 127.0.0.2，
#   而**手机端**引擎用的是服务器下发配置里的 phub_host = pr.m.hub.sandai.net。
#   这里把旧域名直接指到那台 phub，看 hub 查询能不能过。
$BB echo "112.64.218.71 btrouter.sandai.net" >> /etc/hosts
$BB echo "112.64.218.71 master.wap.dphub.sandai.net" >> /etc/hosts
$BB echo "112.64.218.71 hub5p.sandai.net" >> /etc/hosts
$BB echo "112.64.218.71 pr.m.hub.sandai.net" >> /etc/hosts
$BB cat /etc/hosts
$BB ip addr show eth0 2>&1 | $BB head -6
echo "=== [init] 连通性探测 ==="
$BB ping -c 2 -W 3 10.0.2.2 2>&1 | $BB tail -3
echo "=== [init] bionic 网络自检 ==="
/dnstest
echo "=== [init] 引擎写的 setting.cfg ==="
$BB cat /thunder-data/setting.cfg 2>/dev/null
echo "=== [init] 启动 harness（后台）==="
export MAGNET="magnet:?xt=urn:btih:D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7"
export FILENAME="ubuntu-24.04.3-desktop-amd64.iso"
export MON_SECS="0"
export URL=""
export URL_NAME=""
export P2SP_SECS="0"
export DL_SECS="0"
export PROXY_PORT="20080"
export CTRL_PORT="18080"
# ★ 数据面块设备：harness 把引擎吐出的字节按文件偏移写进它，宿主直读同一物理文件。
#   宿主侧由 QemuHostRuntime 用 -drive file=... 挂上，**并把设备名经 cmdline 的 blkdev= 传进来** ——
#   不能再硬编码 /dev/vda：多挂一块 swap 盘后，若数据面孔缺位，vda 就变成 swap 盘了
#   （harness 会把引擎字节写进交换区，后果严重）。这里留空即让 harness 回退纯转发。
#   ⚠ 绝不能在没有块设备的环境里设错 —— blk_open() 打不开会打印警告并回退纯转发，不影响播放。
export BLK_DEV="$BLKDEV"
# ★ queryConfOnInit：让引擎在 init 时拉取服务器配置（setting.cfg）。
#   手机端引擎靠它拿到 "phub_host": "pr.m.hub.sandai.net"；不拉配置就只能退到
#   已下线的 btrouter/master.wap.dphub → BT hub 查询失败（114004）。
export QCO="${QCO:-1}"
/harness &
HPID=$!
$BB sleep 40
echo "=== [init] t=40s TCP ==="
$BB cat /proc/net/tcp | $BB head -14
echo "=== [init] t=40s UDP ==="
$BB cat /proc/net/udp | $BB head -10
$BB sleep 120
echo "=== [init] t=160s TCP ==="
$BB cat /proc/net/tcp | $BB head -14
echo "=== [init] t=160s UDP ==="
$BB cat /proc/net/udp | $BB head -10
$BB sleep 120
echo "=== [init] t=280s TCP ==="
$BB cat /proc/net/tcp | $BB head -14
echo "=== [init] t=280s UDP ==="
$BB cat /proc/net/udp | $BB head -10
echo "=== [init] 进程 ==="
$BB ps
echo "=== [init] /thunder-data 内容 ==="
$BB ls -la /thunder-data
echo "=== [init] 内存与交换区状况 ==="
$BB free
$BB cat /proc/swaps 2>/dev/null
echo "=== [init] 等待 harness 退出 ==="
wait $HPID
echo "=== [init] 引擎配置 setting.cfg（跑完之后才生成）==="
$BB cat /thunder-data/setting.cfg 2>/dev/null | $BB head -40
echo "=== [init] 引擎日志 Thunder.txt 末尾 ==="
$BB tail -80 /thunder-data/Thunder.txt 2>/dev/null || echo "(无日志文件)"
echo "=== [init] harness 已退出，保持存活以便观察 ==="
$BB sleep 30
