#!/bin/bash
# §6.10 Step6g：读 ProcessSpecialOptions——确认特殊选项判定依据（extraInfo vs 名字）
cd /root/x86guest/artsrc
echo '=== ProcessSpecialOptions 完整实现 ==='
sed -n "$(grep -n 'bool ParsedOptions::ProcessSpecialOptions' parsed_options.cc | head -1 | cut -d: -f1),+55p" parsed_options.cc
