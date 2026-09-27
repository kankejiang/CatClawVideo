#!/bin/bash
# 下载 TVBox 非 Guard 同族 jar（宿主 NonGuardFallbackJars 同源）并列出 spider 类
cd /root/x86guest || exit 1
[ -f fty.jar ] || curl -sL --max-time 60 'https://raw.liucn.cc/box/fty.jar' -o fty.jar
ls -la fty.jar
rm -rf /tmp/fty && mkdir -p /tmp/fty
unzip -o -q fty.jar 'classes*.dex' -d /tmp/fty
for d in /tmp/fty/classes*.dex; do
    strings "$d" | grep -aoE 'Lcom/github/catvod/spider/[A-Za-z0-9]+;'
done | sort -u | head -30
