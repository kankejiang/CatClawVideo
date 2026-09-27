#!/bin/bash
# 重打产品 pkg_initrd.gz：新 harness（CTRL_HOST 环境变量化版）+ init 加 CTRL_HOST export
# 输入：/root/x86guest/thunder/（已解包并替换 harness）、/opt/ndk/build/harness4（新编译）
# 输出：/root/x86guest/pkg_initrd_new.gz
set -e
cd /root/x86guest/thunder || exit 1

# 1) init 注入 CTRL_HOST（幂等）
if ! grep -q 'CTRL_HOST' init; then
    sed -i 's|^export CTRL_PORT="18080"$|export CTRL_PORT="18080"\n# ★ 控制端主机：guest 视角的宿主 loopback = 10.0.2.2（SLIRP 约定）；\n#   harness 侧默认 127.0.0.1（本机直跑调试），guest 部署必须显式指定。\nexport CTRL_HOST="10.0.2.2"|' init
    echo "init 已注入 CTRL_HOST"
else
    echo "init 已有 CTRL_HOST，跳过"
fi
grep -n 'CTRL_HOST' init

# 2) harness 用新编译版（再做一次保险）
cp -f /opt/ndk/build/harness4 ./harness
chmod +x ./harness init bin/busybox 2>/dev/null || true

# 3) 重打包（cpio newc + gzip -9，与原格式一致；README 的 xz 大字典坑不适用 gzip）
find . | cpio -o -H newc --owner 0:0 2>/dev/null | gzip -9 > /root/x86guest/pkg_initrd_new.gz
ls -la /root/x86guest/pkg_initrd_new.gz /root/x86guest/pkg_initrd.gz
