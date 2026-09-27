#!/bin/bash
# x86 guest 里程碑验证：KVM 启动 -> 桥就绪计时 -> ping 握手 -> 保持运行
cd /root/x86guest || exit 1
pkill -f qemu-system-x86 2>/dev/null; sleep 1
# 宿主 DNS 转发（ArtDnsServer 的 python 等价物）：guest 的 dnsshim 路线一走
# CATCLAW_DNS=10.0.2.2:11729（cmdline ctrl= → /init 导出；slirp 的 10.0.2.2 直达
# 宿主 loopback）。⚠ 缺它 guest 解析全挂（壳 UnknownHost → homeContent 秒返空，
# 2026-09-27 定位——用户机由宿主 C# ArtDnsServer 补位，108 没有宿主应用）。
pkill -f dnsfwd.py 2>/dev/null
nohup python3 /root/x86guest/dnsfwd.py 11729 > dnsfwd.log 2>&1 &
T0=$(date +%s)
nohup qemu-system-x86_64 -accel kvm -M q35 -m 2048 -smp 4 \
  -kernel linux-data/boot/vmlinuz-6.1.0-50-amd64 -initrd art_initrd_x64.gz \
  -append "console=ttyS0 guardport=18600 ctrl=11729" \
  -netdev user,id=n0,hostfwd=tcp:127.0.0.1:18600-:18600 -device virtio-net-pci,netdev=n0 \
  -nographic -no-reboot > qemu-boot.log 2>&1 &
echo "qemu 已启动"
READY=0
for i in $(seq 1 40); do
  sleep 3
  RESP=$( { echo '{"id":1,"op":"ping"}'; sleep 2; } | timeout 6 nc -w 5 127.0.0.1 18600 2>/dev/null)
  if echo "$RESP" | grep -q '"ok"'; then
    NOW=$(date +%s); READY=1
    echo "★ 桥就绪！耗时 $((NOW-T0))s  应答: $RESP"
    break
  fi
done
if [ $READY -eq 0 ]; then
  echo "120s 内未就绪，日志尾部："
  tail -12 qemu-boot.log | cut -c1-150
else
  echo "==== 桥就绪后的日志 ===="
  tail -6 qemu-boot.log | cut -c1-150
fi