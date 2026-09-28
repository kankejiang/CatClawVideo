#!/bin/bash
# §6.10 Step8e：产品独有清单（非 system/bin）+ md5 比对
cd /root/x86guest
echo '=== 产品版独有（排除 system/bin/，最多 60 条）==='
comm -23 /tmp/list_prod.txt /tmp/list_tree.txt | grep -v '^system/bin/' | head -60
echo
echo "产品独有总数: $(comm -23 /tmp/list_prod.txt /tmp/list_tree.txt | wc -l)"
echo "art-tree 独有总数: $(comm -13 /tmp/list_prod.txt /tmp/list_tree.txt | wc -l)"
echo
echo '=== boot 产物 md5 对比 ==='
echo '-- 产品版 --'
( cd /tmp && rm -rf mp && mkdir mp && cd mp && zstd -dc /root/x86guest/art_initrd.gz 2>/dev/null | cpio -id --quiet 'system/framework/arm64/boot.art' 'system/framework/arm64/boot.oat' 'data/dalvik-cache/arm64/gb.dex' 2>/dev/null
  md5sum system/framework/arm64/boot.art system/framework/arm64/boot.oat data/dalvik-cache/arm64/gb.dex 2>/dev/null )
echo '-- art-tree 版 --'
( cd /tmp && rm -rf mt && mkdir mt && cd mt && zstd -dc /root/x86guest/art_initrd_boottest.gz 2>/dev/null | cpio -id --quiet 'system/framework/arm64/boot.art' 'system/framework/arm64/boot.oat' 'data/dalvik-cache/arm64/gb.dex' 2>/dev/null
  md5sum system/framework/arm64/boot.art system/framework/arm64/boot.oat data/dalvik-cache/arm64/gb.dex 2>/dev/null )
