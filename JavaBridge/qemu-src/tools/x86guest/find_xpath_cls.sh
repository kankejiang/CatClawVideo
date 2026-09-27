#!/bin/bash
# 从 TVBox apk 的 dex 里找 XPath 系类名
rm -rf /tmp/tvbox && mkdir -p /tmp/tvbox
unzip -o -q /root/x86guest/TVBox_debug-java64.apk 'classes*.dex' -d /tmp/tvbox
for d in /tmp/tvbox/classes*.dex; do
    strings "$d" | grep -aoE 'Lcom/github/catvod/spider/X[a-zA-Z]+;' | sort -u | head -8
done
