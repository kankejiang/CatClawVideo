#!/usr/bin/env python3
"""新 site key 强制重装载：用「玩偶2」作为新 site load 同一个 WoGGGuard，
验证「真正的新实例（新 ClassLoader）」能否抢回 6678 并完成播放注册。

背景：同 site 重 load 被桥的幂等缓存挡下（12ms 返回）；本案验证 force 语义的价值
（修复方案：宿主播放前对 Guard 家族源做 force reload）。
用法：python debug_twosite.py <vid> <jarURL>
"""
import json
import os
import sys
import time
from urllib.parse import quote, urlparse

BASE = os.path.join(os.environ["APPDATA"], "CatClawVideo.debug", "javabridge")
IN = os.path.join(BASE, "bridge-debug-in.jsonl")
OUT = os.path.join(BASE, "bridge-debug-out.log")
VID = sys.argv[1] if len(sys.argv) > 1 else "131872"
JAR = sys.argv[2] if len(sys.argv) > 2 else "http://10.0.2.2:2683/jar/08a27c1fff2c064ae9a12c34"
SITE2 = "玩偶2"


def send(obj, wait=180):
    line = json.dumps(obj, ensure_ascii=False)
    with open(IN, "a", encoding="utf-8") as f:
        f.write(line + "\n")
    t0 = time.time()
    while time.time() - t0 < wait:
        time.sleep(0.3)
        if not os.path.exists(OUT):
            continue
        txt = open(OUT, encoding="utf-8", errors="replace").read()
        i = txt.rfind("<- " + line)
        if i >= 0:
            seg = txt[i:].split("\n")
            if len(seg) >= 2 and seg[1].strip():
                return seg[1]
    return None


_n = [10000]


def call(site, method, args, wait=180):
    _n[0] += 1
    r = send({"id": _n[0], "op": "call", "site": site, "method": method, "args": args}, wait)
    if not r:
        return None
    try:
        return json.loads(r.split("ms ", 1)[1] if "ms " in r else "{}")
    except Exception:
        return None


def jp(v):
    try:
        return json.loads(v) if isinstance(v, str) else (v or {})
    except Exception:
        return {}


print("... load 玩偶2（新 site key，强制新装载）...")
_n[0] += 1
r = send({"id": _n[0], "op": "load", "site": SITE2, "className": "WoGGGuard",
          "ext": "{\"Cloud-drive\":\"tvfan/Cloud-drive.txt\"}", "jars": [JAR]}, wait=240)
print("load:", json.dumps(str(r)[:160] if r else "NO-RESP", ensure_ascii=True))
time.sleep(2)

o = call(SITE2, "detailContent", [VID])
item = (jp(o.get("result")).get("list") or [{}])[0]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
if not froms or not urls:
    print("detail 失败:", json.dumps(str(item)[:150], ensure_ascii=True))
    sys.exit(1)
_, _, eid = urls[0].split("#")[0].partition("$")
o = call(SITE2, "playerContent", [froms[0], eid])
url = jp(o.get("result")).get("url")
print("play url:", json.dumps(str(url)[:90], ensure_ascii=True))
if not url:
    sys.exit("playerContent 失败")
path = quote(urlparse(url).path, safe="/")

for tag in ["T+0", "T+6s"]:
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": 300}, wait=60)
    if not r:
        st = "NO-RESP"
    elif "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    print("%-6s: %s" % (tag, st))
    if tag == "T+0":
        time.sleep(6)
print("done")
