#!/bin/bash
# §6.10 Step6i：ProcessSpecialOptions else 分支 + guest 日志里的 special option 警告
cd /root/x86guest/artsrc
echo '=== ProcessSpecialOptions 后半（else 分支）==='
start=$(grep -n 'bool ParsedOptions::ProcessSpecialOptions' parsed_options.cc | head -1 | cut -d: -f1)
sed -n "$((start+55)),$((start+115))p" parsed_options.cc
echo
echo '=== guest 日志里 special/unknown option 警告 ==='
grep -aiE 'special option|unknown option|Ignoring option' /root/x86guest/boottest.log | head -6
grep -aiE 'special option|unknown option' /tmp/fakelogd.log | head -6
