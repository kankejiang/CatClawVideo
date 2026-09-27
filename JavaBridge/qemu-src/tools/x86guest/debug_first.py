#!/usr/bin/env python3
"""决定性实验：干净服务上的「第一个请求」特征是否决定服务健康。

背景：R1(raw)→R2(okhttp 特征)→R3 挂 vs P_A(1B raw)→P_B/P_C(raw) 全活。
假设：第一个请求若是「HTTP/1.1 + Keep-Alive + gzip + Dalvik UA」（Art.java/播放器
的实际特征），会把壳服务带进坏状态；raw（HTTP/1.0）首请求则无恙。

序列（应用重启、玩偶装载后立即跑）：
  playerContent（注册任务）
  P_first  HTTP/1.1 + Keep-Alive + gzip + Dalvik UA，读 300B
  P2       raw HTTP/1.0，读 300B
  P3       raw HTTP/1.0，读 300B
用法：python debug_first.py <vid>
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


_n = [9500]


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


o = call("detailContent", [VID])
item = (jp(o.get("result")).get("list") or [{}])[0]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
if not froms or not urls:
    sys.exit("detail 失败")
_, _, eid = urls[0].split("#")[0].partition("$")
o = call("playerContent", [froms[0], eid])
url = jp(o.get("result")).get("url")
if not url:
    sys.exit("playerContent 失败")
path = quote(urlparse(url).path, safe="/")
print("task:", json.dumps(path[:80], ensure_ascii=True))


def probe(tag, mx, opts):
    _n[0] += 1
    req = {"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
           "path": path, "max": mx}
    req.update(opts)
    r = send(req, wait=60)
    if not r:
        print("%-22s: NO-RESP" % tag)
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:30] if "recv " in r else r[:60]
    print("%-22s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


probe("P_first okhttp特征", 300,
      {"http": "1.1", "keepalive": True, "gzip": True,
       "ua": "Dalvik/2.1.0 (Linux; U; Android 13; CatClawVM)"})
probe("P2 raw", 300, {"http": "1.0"})
probe("P3 raw 复验", 300, {"http": "1.0"})
print("done")
