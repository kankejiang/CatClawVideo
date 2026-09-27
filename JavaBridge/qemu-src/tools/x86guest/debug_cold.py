#!/usr/bin/env python3
"""冷启动全景：装载完成后的服务原始状态 + 「读 1B vs 读满」对服务的影响。

序列（应用重启、玩偶装载完成后跑）：
  P0  直接探 6678（读 300B）——用户未操作时的原始状态
  playerContent（注册任务）
  P_A 读 1 字节就断 —— 「极轻读断」是否打挂服务
  P_B 立即重探（读 300B）
  P_C +5s 再探
用法：python debug_cold.py <vid>
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


_n = [9000]


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


def probe(tag, path, mx):
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": mx}, wait=60)
    if not r:
        print("%-18s: NO-RESP" % tag)
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:30] if "recv " in r else r[:50]
    print("%-18s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


# 用已知路径先做「未注册状态」的原状探针（团圆令路径，任何会话都可用作「未注册样本」）
RAW = "/proxy/play/%E5%A4%B8%E7%88%B6%E7%9B%98/%E5%9B%A2%E5%9C%86%E4%BB%A4/1080p.mp4"
probe("P0 装载后原状", RAW, 300)

path = prep()
if not path:
    sys.exit("playerContent 失败")

probe("P_A 读1B断", path, 1)
probe("P_B 立即重探", path, 300)
time.sleep(5)
probe("P_C +5s", path, 300)
print("done")
