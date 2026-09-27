#!/usr/bin/env python3
"""端口抢占复现：玩偶良好状态下加载 seed（同 Guard jar 家族），看 6678 是否被顶掉。

背景（2026-09-27）：用户失败会话里 MDrive/seed 先于玩偶播放被装载（prefetch 触发），
随后 Found local server port 6678 出现、玩偶拉流 0B。假设：同 jar 家族的多个壳
「抢」约定的 6678 端口——谁最近激活，谁的服务占端口；其它源的播放请求（夸父盘路径）
打到不认识它的服务上 → handler NPE → 0B。

序列：
  P0  探玩偶 path（基线，预期 OK）
  load seed（SeedhubGuard，同 jar）
  P1  探玩偶 path（预期 0B = 被顶）
  玩偶 playerContent 重新注册
  P2  探玩偶 path（看能否恢复）
用法：python debug_takeover.py <玩偶vid> <jarURL>
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


def send(obj, wait=150):
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


_n = [9800]


def call(site, method, args, wait=150):
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


def prep():
    o = call(SITE, "detailContent", [VID])
    item = (jp(o.get("result")).get("list") or [{}])[0]
    froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
    urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
    if not froms or not urls:
        return None
    _, _, eid = urls[0].split("#")[0].partition("$")
    o = call(SITE, "playerContent", [froms[0], eid])
    url = jp(o.get("result")).get("url")
    return quote(urlparse(url).path, safe="/") if url else None


def probe(tag, path, mx=300):
    _n[0] += 1
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": mx}, wait=60)
    if not r:
        print("%-24s: NO-RESP" % tag)
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:26] if "recv " in r else r[:40]
    print("%-24s: %s %s" % (tag, st, json.dumps(recv, ensure_ascii=True)))


# 基线：先注册玩偶任务并探（P0）
path = prep()
if not path:
    sys.exit("玩偶 prep 失败")
probe("P0 基线(玩偶)", path)

# 加载 seed（同 jar 家族）
_n[0] += 1
print("... load seed ...")
r = send({"id": _n[0], "op": "load", "site": "seed", "className": "SeedhubGuard",
          "ext": "5++kwLhNYm9UrO9wh7Dl7eKamTee4s/5", "jars": [JAR]}, wait=180)
print("load seed:", json.dumps(str(r)[:200] if r else "NO-RESP", ensure_ascii=True))
time.sleep(2)

probe("P1 seed装载后", path)

print("... 玩偶重新 playerContent ...")
path2 = prep()
if path2:
    probe("P2 重注册后", path2)
    time.sleep(6)
    probe("P3 +6s", path2)
print("done")
