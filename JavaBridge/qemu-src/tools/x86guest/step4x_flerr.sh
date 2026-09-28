#!/bin/bash
# §6.10 Step4x：从 fakelogd 抓「最后一个 Command 行」之后的第一个报错块
tac /tmp/fakelogd.log | awk '
/Command: / {seen=1}
seen && /does not match|No dex|Failed|Error|error|Unknown|Invalid|not found|Non-zero|Cannot/ {print; c++; if(c>=4) exit}
' | tac
echo '---'
# 兜底：全 log 里 files ( 数字 的最后三次
grep -ao 'files *([0-9]*)' /tmp/fakelogd.log | tail -3
grep -ao 'locations *([0-9]*)' /tmp/fakelogd.log | tail -3
