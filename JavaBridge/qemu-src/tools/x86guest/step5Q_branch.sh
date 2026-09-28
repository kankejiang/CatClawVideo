#!/bin/bash
# §6.10 Step5Q：找可用的 platform_art 分支/标签（下载 image_space.cc）
cd /root/x86guest/artsrc
URL='https://raw.githubusercontent.com/aosp-mirror/platform_art'
F='runtime/gc/space/image_space.cc'
for ref in pie-release android-9.0.0_r1 android-9.0.0_r34 android-9.0.0_r61 pie-dev oreo-release master; do
  code=$(curl -s -o /tmp/t.cc -w '%{http_code}' --max-time 30 "$URL/$ref/$F")
  sz=$(wc -c < /tmp/t.cc)
  echo "$ref -> HTTP $code, $sz 字节"
  if [ "$code" = "200" ] && [ "$sz" -gt 10000 ]; then
    cp /tmp/t.cc image_space.cc
    echo "== 命中：$ref =="
    break
  fi
done
