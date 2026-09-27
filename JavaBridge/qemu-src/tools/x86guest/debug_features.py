#!/usr/bin/env python3
"""请求特征对照实验：定位「哪个 HTTP 特征触发壳服务 6678 拒绝」。

背景：raw HTTP/1.0 探针成功（200+MP4），而 Art.java 的 okhttp（HTTP/1.1 + Keep-Alive
+ Accept-Encoding: gzip + Dalvik UA）报 unexpected end of stream。每轮：
detailContent → playerContent（刷新任务）→ 立即用不同请求特征探针。

用法：python debug_features.py <vid>
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


def send(obj, wait=90):
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


_n = [7000]


def call(method, args, wait=120):
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


def prep():
    """detail + playerContent，返回 play path。"""
    o = call("detailContent", [VID])
    item = (jp(o.get("result")).get("list") or [{}])[0]
    froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
    urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
    if not froms or not urls:
        return None
    _, _, eid = urls[0].split("#")[0].partition("$")
    o = call("playerContent", [froms[0], eid])
    url = jp(o.get("result")).get("url")
    return quote(urlparse(url).path, safe="/") if url else None


def probe(tag, path, opts):
    _n[0] += 1
    req = {"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
           "path": path, "max": 300}
    req.update(opts)
    r = send(req, wait=60)
    if not r:
        print("%-12s: NO-RESP" % tag)
        return
    ok = "HTTP/1.1 200" in r
    recv = r.split("recv ", 1)[1][:36] if "recv " in r else r[:60]
    print("%-12s: %s %s" % (tag, "OK  " if ok else "FAIL", json.dumps(recv, ensure_ascii=True)))


rounds = [
    ("R1 raw(对照)", {"http": "1.0"}),
    ("R2 okhttp特征", {"http": "1.1", "keepalive": True, "gzip": True,
                      "ua": "Dalvik/2.1.0 (Linux; U; Android 13; CatClawVM)"}),
    ("R3 1.1+KA", {"http": "1.1", "keepalive": True}),
    ("R4 1.1+gzip", {"http": "1.1", "gzip": True}),
    ("R5 raw(复验)", {"http": "1.0"}),
]

for name, opts in rounds:
    path = prep()
    if not path:
        print("%-12s: playerContent 失败" % name)
        continue
    probe(name, path, opts)
print("done")
