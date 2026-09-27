#!/bin/bash
# 列出 TVBox apk 全部 dex 里的 com.github.catvod.spider 类
rm -rf /tmp/tvbox && mkdir -p /tmp/tvbox
unzip -o -q /root/x86guest/TVBox_debug-java64.apk 'classes*.dex' -d /tmp/tvbox
for d in /tmp/tvbox/classes*.dex; do
    strings "$d" | grep -aoE 'Lcom/github/catvod/spider/[A-Za-z0-9]+;'
done | sort -u | head -40
