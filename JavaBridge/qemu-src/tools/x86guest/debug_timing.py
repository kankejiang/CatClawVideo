#!/usr/bin/env python3
"""验证「壳流服务 6678 的就绪时机」假设：

背景（2026-09-27）：Art.java 透传在 playerContent 后 ~1s 请求 6678 报 unexpected end of
stream（8 连败）；而 50s 后直接探针 200 OK 全量流。本脚本对「同一部片的另一个条目」
走 playerContent → **立即连打**（间隔 0.5s×6）→ **等 25s 再连打**（6 次），画出就绪曲线。
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
        idx = txt.rfind("<- " + line)
        if idx < 0:
            continue
        seg = txt[idx:].split("\n")
        if len(seg) >= 2 and seg[1].strip():
            return seg[1]
    return None


_n = [2000]


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


def probe(tag, path, port=6678):
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": port,
              "path": path, "max": 256}, wait=45)
    if not r:
        print("%s: 无结果" % tag)
        return False
    ok = "HTTP/1.1 200" in r
    brief = r.split("recv ", 1)[1][:60] if "recv " in r else r[:80]
    print("%s: %s | %s" % (tag, "OK" if ok else "FAIL", brief))
    return ok


# 用「另一个片」（category 第 2 条）避免复用已有条目
o = call("categoryContent", ["1", "1", "false", ""])
lst = jp(o.get("result")).get("list") or []
if len(lst) < 2:
    sys.exit("影片不足")
vid = lst[1].get("vod_id")
print("目标片:", vid, str(lst[1].get("vod_name"))[:30])

o = call("detailContent", [str(vid)])
item = (jp(o.get("result")).get("list") or [{}])[0]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
if not urls:
    sys.exit("无 url")
first = urls[0].split("#")[0]
_, _, eid = first.partition("$")

o = call("playerContent", [froms[0], eid])
pr = jp(o.get("result"))
url = pr.get("url")
print("play url:", url)
if not url:
    sys.exit("无 url")

path = quote(urlparse(url).path, safe="/")
t0 = time.time()
print("=== 立即连打（0.5s 间隔 ×6）===")
for i in range(6):
    probe("T+%.1fs" % (time.time() - t0), path)
    time.sleep(0.5)

print("=== 等 25s 再打（间隔 1s ×6）===")
time.sleep(25)
for i in range(6):
    probe("T+%.1fs" % (time.time() - t0), path)
    time.sleep(1.0)
print("done")
