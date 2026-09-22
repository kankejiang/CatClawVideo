#!/bin/busybox sh
BB=/bin/busybox
export PATH=/bin
$BB mkdir -p /proc /sys /dev /etc /tmp
$BB mount -t proc     proc /proc 2>/dev/null
$BB mount -t sysfs    sys  /sys  2>/dev/null
$BB mount -t devtmpfs dev  /dev  2>/dev/null
# ★ /thunder-data 必须挂**真 tmpfs**：initramfs 的 rootfs 是 ramfs，
#   而 ramfs 的 statvfs 空闲空间上报不可信（通常 0）—— 迅雷（尤其 BT 任务要预分配整个文件）
#   很可能据此判定「磁盘空间不足」，然后**静默不下数据**（任务一直 st=1、零字节）。
$BB mkdir -p /preload
$BB cp -r /thunder-data/. /preload/ 2>/dev/null
$BB mount -t tmpfs -o size=3500m tmpfs /thunder-data
$BB cp -r /preload/. /thunder-data/ 2>/dev/null
echo "=== [init] /thunder-data 已挂 tmpfs（3500m）==="
$BB df -h /thunder-data
echo "=== [init] 引导完成 ==="
echo "=== [init] 内核模块 ==="
# virtio_blk：数据面块设备（引擎吐出的字节按偏移写进它，宿主直读同一文件，实测 2454MB/s）。
# 不是所有环境都会挂这块盘 —— insmod 失败不致命，harness 侧 blk_open() 会自行回退。
for m in failover net_failover virtio_net virtio_blk; do
  if $BB insmod /modules/$m.ko 2>&1; then echo "  insmod $m ... ok"; else echo "  insmod $m ... 失败（不致命）"; fi
done
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
#   宿主侧由 QemuHostRuntime 用 -drive file=... 挂上；这里只告诉 harness 设备节点。
#   ⚠ 绝不能在没有块设备的环境里设错 —— blk_open() 打不开会打印警告并回退纯转发，不影响播放。
export BLK_DEV="/dev/vda"
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
echo "=== [init] 等待 harness 退出 ==="
wait $HPID
echo "=== [init] 引擎配置 setting.cfg（跑完之后才生成）==="
$BB cat /thunder-data/setting.cfg 2>/dev/null | $BB head -40
echo "=== [init] 引擎日志 Thunder.txt 末尾 ==="
$BB tail -80 /thunder-data/Thunder.txt 2>/dev/null || echo "(无日志文件)"
echo "=== [init] harness 已退出，保持存活以便观察 ==="
$BB sleep 30
