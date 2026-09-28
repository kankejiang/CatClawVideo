#!/bin/bash
# §6.10 Step8d：init diff 全文 + boot 产物 md5 + dalvik-cache 预置一致性
cd /root/x86guest
echo '=== ① init diff 完整（忽略注释行）==='
diff /tmp/ip/init_prod.sh /tmp/it/init_tree.sh | grep -av '^[<>] #' | grep -av '^---$' | head -60

echo
echo '=== ② init 关键行对比 ==='
for f in /tmp/ip/init_prod.sh /tmp/it/init_tree.sh; do
  echo "-- $f --"
  grep -n 'CATCLAW_BCP_LOCATIONS=\|CATCLAW_JVM_EXTRA=\|bootimg\|artlaunch bridge' "$f" | head -6
done

echo
echo '=== ③ 两版 initrd 里 boot.art/boot.oat 的 md5（应为同一批产物）==='
( cd /tmp/ip && zstd -dc /root/x86guest/art_initrd.gz | cpio -id --quiet 'system/framework/arm64/boot.art' 'system/framework/arm64/boot.oat' 'data/dalvik-cache/arm64/gb.dex' 2>/dev/null; md5sum system/framework/arm64/boot.art system/framework/arm64/boot.oat data/dalvik-cache/arm64/gb.dex 2>/dev/null )
( cd /tmp/it && zstd -dc /root/x86guest/art_initrd_boottest.gz | cpio -id --quiet 'system/framework/arm64/boot.art' 'system/framework/arm64/boot.oat' 'data/dalvik-cache/arm64/gb.dex' 2>/dev/null; md5sum system/framework/arm64/boot.art system/framework/arm64/boot.oat data/dalvik-cache/arm64/gb.dex 2>/dev/null )
