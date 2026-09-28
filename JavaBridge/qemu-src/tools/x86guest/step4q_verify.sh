#!/bin/bash
# §6.10 Step4q：确认 15 jar 的注入状态（classes.dex 应为标准 dex 039）
FW=/root/x86guest/art-tree/system/framework
cd /tmp && rm -rf mchk3 && mkdir mchk3 && cd mchk3
for j in core-oj core-libart framework ext; do
  unzip -o -q $FW/$j.jar classes.dex -d $j 2>/dev/null
  m=$(od -A n -t x1 -N 8 $j/classes.dex 2>/dev/null | tr -d ' ')
  echo "$j.jar classes.dex magic=$m size=$(stat -c%s $j/classes.dex 2>/dev/null)"
done
unzip -o -q $FW/framework.jar classes2.dex -d fw2 2>/dev/null && echo "framework classes2: $(od -A n -t x1 -N 4 fw2/classes2.dex | tr -d ' ')"
