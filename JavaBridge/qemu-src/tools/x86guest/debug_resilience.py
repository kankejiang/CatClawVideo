#!/usr/bin/env python3
"""韧性实验：壳服务 6678 在被「读一段就断」后多久恢复/如何恢复。

背景（2026-09-27）：R1/R2 探针（读 300B 后客户端断开）成功，R3 起服务挂
（ConnectException/0B）。本脚本量化：断开后 0.5s/10s/30s 的重探结果，以及
「重新 playerContent（刷新任务注册）」能否立即恢复。

用法：python debug_resilience.py <vid>
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


_n = [8000]


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


def probe(tag, path, mx=300):
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": mx}, wait=60)
    if not r:
        print("%-16s: NO-RESP" % tag)
        return
    ok = "HTTP/1.1 200" in r
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif ok:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:30] if "recv " in r else r[:50]
    print("%-16s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


path = prep()
if not path:
    sys.exit("playerContent 失败")

probe("P1 读满断", path)
time.sleep(0.5)
probe("P2 +0.5s", path)
time.sleep(10)
probe("P3 +10.5s", path)
time.sleep(20)
probe("P4 +30s", path)

print("... 重新 playerContent（刷新任务）...")
path2 = prep()
if path2:
    probe("P5 刷新后立即", path2)
    time.sleep(8)
    probe("P6 +8s", path2)
print("done")
