#!/usr/bin/env python3
"""驱动宿主「桥调试直通」跑玩偶全链 + 6678 取证。

前置：应用带 CATCLAW_BRIDGE_DEBUG=1 重启（宿主会轮询 bridge-debug-in.jsonl 并回填
bridge-debug-out.log）；玩偶由宿主自动装载（等约 40s）。本脚本逐步：homeContent →
categoryContent → detailContent → playerContent → fetch 探壳流服务 6678/9978。
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


def send(obj, wait=180):
    line = json.dumps(obj, ensure_ascii=False)
    with open(IN, "a", encoding="utf-8") as f:
        f.write(line + "\n")
    print(">>", line[:140])
    t0 = time.time()
    while time.time() - t0 < wait:
        time.sleep(1.5)
        if not os.path.exists(OUT):
            continue
        txt = open(OUT, encoding="utf-8", errors="replace").read()
        idx = txt.rfind("<- " + line)
        if idx < 0:
            continue
        seg = txt[idx:].split("\n")
        if len(seg) >= 2 and seg[1].strip():
            print("<<", seg[1][:600])
            return seg[1]
    print("!! 超时")
    return None


_n = [1000]


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


o = None
for i in range(12):
    o = call("homeContent", [""], wait=120)
    if o and o.get("ok"):
        break
    print("... homeContent 未就绪，重试", i)
    time.sleep(5)
if not o or not o.get("ok"):
    sys.exit("homeContent 失败")

cats = jp(o.get("result")).get("class") or []
print("分类:", [(c.get("type_id"), c.get("type_name")) for c in cats[:6]])
if not cats:
    sys.exit("无分类")
tid = cats[0].get("type_id")

o = call("categoryContent", [str(tid), "1", "false", ""])
lst = jp(o.get("result")).get("list") or []
print("影片数:", len(lst))
if not lst:
    sys.exit("无影片")
vid = lst[0].get("vod_id")
print("首片:", vid, str(lst[0].get("vod_name"))[:30])

o = call("detailContent", [str(vid)])
item = (jp(o.get("result")).get("list") or [{}])[0]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
print("源:", froms[:4])
if not froms or not urls:
    sys.exit("无播放源")
first = urls[0].split("#")[0]
title, _, eid = first.partition("$")
print("集名=%r id 长度=%d" % (title[:40], len(eid)))

o = call("playerContent", [froms[0], eid])
pr = jp(o.get("result"))
url = pr.get("url")
print("play url:", url)
if not url:
    sys.exit("无 play url")

path = quote(urlparse(url).path, safe="/")
for port, p, mx, note in [(6678, "/", 512, "6678 根"), (9978, "/", 256, "9978 对照"),
                          (6678, path, 1024, "6678 play"), (6678, path, 1024, "6678 play#2")]:
    _n[0] += 1
    print("== probe", note)
    send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": port, "path": p, "max": mx})
print("完成")
