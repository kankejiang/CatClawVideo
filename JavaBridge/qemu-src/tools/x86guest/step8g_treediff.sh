#!/bin/bash
# §6.10 Step8g：两版 initrd 完整解包 + diff -rq（最彻底的文件级对比）
cd /root/x86guest
df -h /root | tail -1
rm -rf cmp && mkdir -p cmp/tree cmp/prod
( cd cmp/tree && zstd -dc /root/x86guest/art_initrd_boottest.gz 2>/dev/null | cpio -idm --quiet 2>/dev/null )
( cd cmp/prod && zstd -dc /root/x86guest/art_initrd.gz 2>/dev/null | cpio -idm --quiet 2>/dev/null )
echo "tree 解包: $(du -sh cmp/tree | cut -f1)，prod 解包: $(du -sh cmp/prod | cut -f1)"
echo
echo '=== diff -rq（仅报告中差异项；tree 为左、prod 为右）==='
diff -rq cmp/tree cmp/prod 2>/dev/null | head -80
echo
echo '=== 差异项统计 ==='
diff -rq cmp/tree cmp/prod 2>/dev/null | wc -l
