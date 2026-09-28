#!/bin/bash
# §6.10 Step4f：诊断 dex2oatd 卡死（CPU 0%）+ 清掉占 CPU 的旧 qemu-system
cd /root/x86guest
echo '=== 旧 qemu-system（清理）==='
for p in $(pgrep -f 'qemu-system'); do
  ps -o pid,etime,cmd -p "$p" --no-headers | head -1
  kill "$p" 2>/dev/null
done
sleep 2
pgrep -cf qemu-system || echo 'qemu-system 已清'

echo '=== dex2oatd 进程状态 ==='
DPID=$(pgrep -f 'dex2oatd' | head -1)
echo "PID=$DPID"
if [ -n "$DPID" ]; then
  grep -E 'State|Threads' /proc/$DPID/status
  echo '--- 各线程内核等待点（wchan）---'
  for t in /proc/$DPID/task/*; do
    echo "$(basename $t): $(cat $t/wchan 2>/dev/null) $(cat $t/comm 2>/dev/null)"
  done | head -12
  echo '--- 最近系统调用（strace 5 秒采样）---'
  which strace >/dev/null && timeout 5 strace -c -f -p $DPID 2>&1 | tail -15 || echo '（无 strace）'
fi
