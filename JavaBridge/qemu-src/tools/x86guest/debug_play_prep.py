#!/usr/bin/env python3
"""vid → detailContent → playerContent → 探针系列（复现/推翻「就绪窗口」假设）。

用法：python debug_play_prep.py <vid> [等待秒=50]

对每个 vid：走完整播放准备（与宿主播放链路相同），然后
  ① 立即探 6678（1 次）—— 模拟播放器「playerContent 后立刻拉流」
  ② 等 N 秒
  ③ 再探 6678（3 次，间隔 2s）—— 看是否出现 200 就绪窗口
输出全 ASCII（URL 编码路径原样显示）。
"""
import json
import os
import sys
import time
from urllib.parse import quote, urlparse

BASE = os.path.join(os.environ["APPDATA"], "CatClawVideo.debug", "javabridge")
IN = os.path.join(BASE, "bridge-debug-in.jsonl")
OUT = os.path.join(BASE, "bridge-debug-out.log")
SITE = "玩偶"
VID = sys.argv[1] if len(sys.argv) > 1 else "131872"
WAIT = int(sys.argv[2]) if len(sys.argv) > 2 else 50


def send(obj, wait=120):
    line = json.dumps(obj, ensure_ascii=False)
    with open(IN, "a", encoding="utf-8") as f:
        f.write(line + "\n")
    t0 = time.time()
    while time.time() - t0 < wait:
        time.sleep(1.0)
        if not os.path.exists(OUT):
            continue
        txt = open(OUT, encoding="utf-8", errors="replace").read()
        i = txt.rfind("<- " + line)
        if i >= 0:
            seg = txt[i:].split("\n")
            if len(seg) >= 2 and seg[1].strip():
                return seg[1]
    return None


_n = [4000]


def call(method, args, wait=180):
    _n[0] += 1
    r = send({"id": _n[0], "op": "call", "site": SITE, "method": method, "args": args}, wait)
    if not r:
        return None
    body = r.split("ms ", 1)[1] if "ms " in r else "{}"
    try:
        return json.loads(body)
    except Exception:
        return None


def jp(v):
    try:
        return json.loads(v) if isinstance(v, str) else (v or {})
    except Exception:
        return {}


o = call("detailContent", [VID])
item = (jp(o.get("result")).get("list") or [{}])[0]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
print("vod_name:", json.dumps(str(item.get("vod_name", ""))[:40], ensure_ascii=True))
if not froms or not urls:
    sys.exit("无播放源")
first = urls[0].split("#")[0]
title, _, eid = first.partition("$")
print("flag:", json.dumps(froms[0], ensure_ascii=True), "ep:", json.dumps(title[:50], ensure_ascii=True))

o = call("playerContent", [froms[0], eid])
pr = jp(o.get("result"))
url = pr.get("url")
print("play url:", json.dumps(url, ensure_ascii=True))
if not url:
    sys.exit("无 play url")

path = quote(urlparse(url).path, safe="/")
t0 = time.time()


def probe(tag):
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": 256}, wait=60)
    if not r:
        print("%s: NO-RESP" % tag)
        return
    body = r.split("ms ", 1)
    ms = body[0].split()[-1] if body else "?"
    ok = "HTTP/1.1 200" in r
    recv = r.split("recv ", 1)[1][:40] if "recv " in r else r[:60]
    print("%s: %s %sms %s" % (tag, "OK " if ok else "FAIL", ms, json.dumps(recv, ensure_ascii=True)))


probe("T+%.0fs 立即" % (time.time() - t0))
print("... 等 %ds ..." % WAIT)
time.sleep(WAIT)
probe("T+%.0fs #1" % (time.time() - t0))
time.sleep(2)
probe("T+%.0fs #2" % (time.time() - t0))
time.sleep(2)
probe("T+%.0fs #3" % (time.time() - t0))
print("done")
