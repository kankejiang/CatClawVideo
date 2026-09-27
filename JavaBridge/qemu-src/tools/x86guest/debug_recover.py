#!/usr/bin/env python3
"""恢复验证：重新 load 玩偶（重抢 6678）能否让其播放恢复；再 load seed 是否又被顶。

承接 debug_takeover 的结论（seed 装载顶掉玩偶 6678、playerContent 无法恢复）：
  1. load 玩偶（重）→ playerContent → 探   （预期 OK = 抢回成功）
  2. load seed（再）    → 探                 （预期 0B = 又被顶）
  3. load 玩偶（再）→ playerContent → 探     （预期 OK = 确定性双向）
用法：python debug_recover.py <vid> <jarURL>
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
JAR = sys.argv[2] if len(sys.argv) > 2 else "http://10.0.2.2:2683/jar/08a27c1fff2c064ae9a12c34"


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


_n = [9900]


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


def load(site, cls, ext):
    _n[0] += 1
    r = send({"id": _n[0], "op": "load", "site": site, "className": cls,
              "ext": ext, "jars": [JAR]}, wait=180)
    print("load %s: %s" % (site, json.dumps(str(r)[:120] if r else "NO-RESP", ensure_ascii=True)))


def prep_probe(tag):
    o = call("detailContent", [VID])
    item = (jp(o.get("result")).get("list") or [{}])[0]
    froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
    urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
    if not froms or not urls:
        print("%-22s: prep 失败" % tag)
        return
    _, _, eid = urls[0].split("#")[0].partition("$")
    o = call("playerContent", [froms[0], eid])
    url = jp(o.get("result")).get("url")
    if not url:
        print("%-22s: playerContent 失败" % tag)
        return
    path = quote(urlparse(url).path, safe="/")
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": 300}, wait=60)
    if not r:
        print("%-22s: NO-RESP" % tag)
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:26] if "recv " in r else r[:40]
    print("%-22s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


print("=== 步骤 1：重 load 玩偶 → 播放 ===")
load(SITE, "WoGGGuard", "{\"Cloud-drive\":\"tvfan/Cloud-drive.txt\"}")
time.sleep(2)
prep_probe("S1 重load玩偶后")

print("=== 步骤 2：再 load seed → 探 ===")
load("seed", "SeedhubGuard", "5++kwLhNYm9UrO9wh7Dl7eKamTee4s/5")
time.sleep(2)
# 直接探（任务已在？先重 playerContent 更贴近现实）
o = call("detailContent", [VID])
item = (jp(o.get("result")).get("list") or [{}])[0]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
if froms and urls:
    _, _, eid = urls[0].split("#")[0].partition("$")
    o = call("playerContent", [froms[0], eid])
    url = jp(o.get("result")).get("url")
    if url:
        path = quote(urlparse(url).path, safe="/")
        _n[0] += 1
        r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
                  "path": path, "max": 300}, wait=60)
        st = ("CONNREFUSED" if (r and "ConnectException" in r)
              else "OK" if (r and "HTTP/1.1 200" in r) else "FAIL-0B")
        print("%-22s: %s" % ("S2 seed再装后", st))

print("=== 步骤 3：再 load 玩偶 → 播放（确定性） ===")
load(SITE, "WoGGGuard", "{\"Cloud-drive\":\"tvfan/Cloud-drive.txt\"}")
time.sleep(2)
prep_probe("S3 再抢回后")
print("done")
