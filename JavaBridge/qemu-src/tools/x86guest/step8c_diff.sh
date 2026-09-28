#!/bin/bash
# §6.10 Step8c：产品版(11s) vs art-tree 版(41s) initrd 差异——定位 ART 段 6 倍差的来源
cd /root/x86guest
echo '=== ① 文件清单 diff ==='
zstd -dc art_initrd.gz 2>/dev/null | cpio -it 2>/dev/null | sort > /tmp/list_prod.txt
zstd -dc art_initrd_boottest.gz 2>/dev/null | cpio -it 2>/dev/null | sort > /tmp/list_tree.txt
echo "产品版条目: $(wc -l < /tmp/list_prod.txt)，art-tree 版条目: $(wc -l < /tmp/list_tree.txt)"
echo "-- 产品独有（前 20）--"
comm -23 /tmp/list_prod.txt /tmp/list_tree.txt | head -20
echo "-- art-tree 独有（前 30）--"
comm -13 /tmp/list_prod.txt /tmp/list_tree.txt | head -30

echo
echo '=== ② init 脚本 diff ==='
(cd /tmp && rm -rf ip && mkdir ip && cd ip && zstd -dc /root/x86guest/art_initrd.gz | cpio -id --quiet init 2>/dev/null; mv init init_prod.sh)
(cd /tmp && rm -rf it && mkdir it && cd it && zstd -dc /root/x86guest/art_initrd_boottest.gz | cpio -id --quiet init 2>/dev/null; mv init init_tree.sh)
diff /tmp/ip/init_prod.sh /tmp/it/init_tree.sh | head -50
