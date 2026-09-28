#!/bin/bash
# §6.10 Step4a v2：多源获取 vdexExtractor（github 直连在 108 上挂了：HTTP2 framing layer）
cd /root/x86guest
rm -rf vdexExtractor vdexExtractor-master.zip

ok=0
# ① codeload zip（与 github.com 不同路由，常能活）
for u in https://codeload.github.com/anestisb/vdexExtractor/zip/refs/heads/master; do
  echo "== try $u =="
  curl -sL --max-time 60 -o vdexExtractor-master.zip "$u" && [ -s vdexExtractor-master.zip ] && ok=1 && break
done
# ② git clone 镜像
if [ $ok -eq 0 ]; then
  for m in https://ghproxy.net/https://github.com/anestisb/vdexExtractor.git \
           https://gh-proxy.com/https://github.com/anestisb/vdexExtractor.git \
           https://gitclone.com/github.com/anestisb/vdexExtractor.git; do
    echo "== try clone $m =="
    if git clone --depth 1 "$m" vdexExtractor 2>&1 | tail -1; [ -d vdexExtractor/.git ]; then ok=1; break; fi
    rm -rf vdexExtractor
  done
fi

if [ $ok -eq 0 ]; then echo '!!! 全部源失败'; exit 1; fi

# 解 zip（如果走的是 ①）
if [ ! -d vdexExtractor ] && [ -s vdexExtractor-master.zip ]; then
  unzip -q vdexExtractor-master.zip && mv vdexExtractor-master vdexExtractor
fi

echo "== 源码就位 =="
ls vdexExtractor | head

echo "== make =="
cd vdexExtractor
make -j4 2>&1 | tail -6
ls -la vdexExtractor
echo "== 抽自检（--help）=="
./vdexExtractor --help 2>&1 | head -10 || true
