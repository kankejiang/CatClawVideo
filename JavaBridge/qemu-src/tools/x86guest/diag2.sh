#!/bin/bash
# rootfs 扫描：定位 namespace abort 字符串宿主 + 模块与配置核对
cd /root/x86guest || exit 1
echo '==== init insmod 循环（核对版本）===='
grep 'for m in' /root/x86guest/mk_x86_initrd.sh
echo '==== rootfs/modules 实际内容 ===='
ls -la rootfs/modules/ | tail -11
echo '==== 谁带 namespace 字符串 ===='
for f in rootfs/artlaunch rootfs/proppreload.so rootfs/fakelogd rootfs/system/bin/linker64 rootfs/system/lib64/libart.so rootfs/apex/com.android.art/lib64/libart.so rootfs/apex/com.android.art/lib64/libnativebridge.so rootfs/apex/com.android.runtime/lib64/bionic/linker64; do
    if [ -f "$f" ]; then
        c=$(grep -c 'namespace for loading' "$f" 2>/dev/null)
        echo "hit $c : $f"
    else
        echo "missing: $f"
    fi
done
echo '==== full rootfs scan ===='
grep -rl 'namespace for loading' rootfs/ 2>/dev/null | head -6
echo '==== ld.config in initrd ===='
zcat art_initrd_x64.gz | cpio -it 2>/dev/null | grep -c 'linkerconfig/ld.config.txt'
