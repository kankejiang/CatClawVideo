#!/usr/bin/env python3
"""重启窗口曲线：playerContent 后 T+0.2/1/2/5s 各探一次 6678。

假设（2026-09-27）：playerContent 触发壳「抢回 6678 服务」（重启 1-2s），播放器
0.1s 后拉流撞上窗口 → 0B；探针因轮询延迟（1s+）错开窗口 → 200。
用法：python debug_window.py <vid>
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
        time.sleep(0.2)          # 0.2s 轮询（低延迟）
        if not os.path.exists(OUT):
            continue
        txt = open(OUT, encoding="utf-8", errors="replace").read()
        i = txt.rfind("<- " + line)
        if i >= 0:
            seg = txt[i:].split("\n")
            if len(seg) >= 2 and seg[1].strip():
                return seg[1]
    return None


_n = [9600]


def jp(v):
    try:
        return json.loads(v) if isinstance(v, str) else (v or {})
    except Exception:
        return {}


def call(method, args, wait=120):
    _n[0] += 1
    r = send({"id": _n[0], "op": "call", "site": SITE, "method": method, "args": args}, wait)
    if not r:
        return None
    try:
        return json.loads(r.split("ms ", 1)[1] if "ms " in r else "{}")
    except Exception:
        return None


o = call("detailContent", [VID])
item = (jp(o.get("result")).get("list") or [{}])[0]
froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
if not froms or not urls:
    sys.exit("detail 失败")
_, _, eid = urls[0].split("#")[0].partition("$")

# 用 call 拿 playerContent（马上拿到结果），随后立刻计时探针
_n[0] += 1
t_pc = time.time()
r = send({"id": _n[0], "op": "call", "site": SITE, "method": "playerContent",
          "args": [froms[0], eid]}, wait=120)
t_done = time.time()
o = json.loads(r.split("ms ", 1)[1] if "ms " in r else "{}")
url = jp(o.get("result")).get("url")
if not url:
    sys.exit("playerContent 失败")
path = quote(urlparse(url).path, safe="/")
print("playerContent 返回耗时 %.2fs" % (t_done - t_pc))


def probe(tag):
    _n[0] += 1
    tt = time.time() - t_done
    r = send({"id": _n[0], "op": "fetch", "host": "127.0.0.1", "port": 6678,
              "path": path, "max": 300}, wait=60)
    t2 = time.time() - t_done
    if not r:
        print("T+%.2f→%.2fs %-12s: NO-RESP" % (tt, t2, tag))
        return
    if "ConnectException" in r:
        st = "CONNREFUSED"
    elif "HTTP/1.1 200" in r:
        st = "OK"
    else:
        st = "FAIL-0B"
    recv = r.split("recv ", 1)[1][:26] if "recv " in r else r[:40]
    print("T+%.2f→%.2fs %-12s: %s %s" % (tt, t2, tag, st, json.dumps(recv, ensure_ascii=True)))


probe("~0.2s")
time.sleep(max(0, 1.0 - (time.time() - t_done) - 0.05))
probe("~1s")
time.sleep(max(0, 2.0 - (time.time() - t_done) - 0.05))
probe("~2s")
time.sleep(max(0, 5.0 - (time.time() - t_done) - 0.05))
probe("~5s")
print("done")
