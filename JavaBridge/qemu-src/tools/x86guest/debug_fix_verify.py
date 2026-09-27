#!/usr/bin/env python3
"""修复验证：seed 顶掉玩偶 → force 重装载玩偶抢回 6678 → 播放注册可用。

用法：python debug_fix_verify.py <vid> <jarURL>
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
JAR = sys.argv[2] if len(sys.argv) > 2 else "http://10.0.2.2:8540/jar/08a27c1fff2c064ae9a12c34"


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


_n = [11000]


def call(method, args, wait=150):
    _n[0] += 1
    r = send({"id": _n[0], "op": "call", "site": SITE, "method": method, "args": args}, wait)
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
        print("%-26s: NO-RESP" % tag)
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:26] if "recv " in r else r[:40]
    print("%-26s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


# 1. 基线
path = prep()
if not path:
    sys.exit("基线 prep 失败")
probe("V0 基线(玩偶)", path)

# 2. load seed 顶掉
_n[0] += 1
print("... load seed（模拟用户浏览其它 Guard 源）...")
r = send({"id": _n[0], "op": "load", "site": "seed", "className": "SeedhubGuard",
          "ext": "5++kwLhNYm9UrO9wh7Dl7eKamTee4s/5", "jars": [JAR]}, wait=240)
print("load seed: %s" % json.dumps(str(r)[:110] if r else "NO-RESP", ensure_ascii=True))
time.sleep(2)
path = prep()
probe("V1 seed顶掉后", path)

# 3. force 重装载玩偶（模拟宿主 Guard 端口守卫）
_n[0] += 1
print("... force 重装载玩偶（模拟宿主守卫）...")
t0 = time.time()
r = send({"id": _n[0], "op": "load", "site": SITE, "className": "WoGGGuard", "force": True,
          "ext": "{\"Cloud-drive\":\"tvfan/Cloud-drive.txt\"}", "jars": [JAR]}, wait=240)
print("force load: %.1fs %s" % (time.time() - t0,
      json.dumps(str(r)[:110] if r else "NO-RESP", ensure_ascii=True)))
time.sleep(2)
path = prep()
probe("V2 force抢回后", path)
time.sleep(6)
probe("V3 +6s 保持", path)
print("done")
