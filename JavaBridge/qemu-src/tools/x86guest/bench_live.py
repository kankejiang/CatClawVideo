#!/usr/bin/env python3
"""对「应用中正在运行的 ART guest 桥」直接取证（不 load——宿主应用已装载玩偶）：

ping → netstat → homeContent → categoryContent → detailContent → playerContent
→ fetch 探壳流服务 6678（拿「unexpected end of stream」的原始字节证据）。

用法：python bench_live.py [桥端口=18600] [site key=玩偶]
"""
import socket
import sys
import json
import time
from urllib.parse import quote, urlparse

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18600
SITE = sys.argv[2] if len(sys.argv) > 2 else "玩偶"

s = socket.create_connection(("127.0.0.1", PORT), timeout=15)
f = s.makefile("rwb")


def rpc(obj, timeout=300):
    f.write((json.dumps(obj) + "\n").encode())
    f.flush()
    t0 = time.time()
    while True:
        line = f.readline()
        if not line:
            raise RuntimeError("EOF")
        resp = json.loads(line)
        if resp.get("id") == obj["id"]:
            return resp, time.time() - t0


def jparse(v):
    try:
        return json.loads(v) if isinstance(v, str) else (v or {})
    except Exception:
        return {}


r, t = rpc({"id": 1, "op": "ping"})
print("ping: %.3fs %s" % (t, r.get("result")))

r, _ = rpc({"id": 2, "op": "netstat"})
print("netstat: %r" % str(r.get("result"))[:400])


def call(mid, method, args, timeout=300):
    r, t = rpc({"id": mid, "op": "call", "site": SITE, "method": method, "args": args}, timeout)
    print("%s: %.3fs ok=%s %s" % (method, t, r.get("ok"), str(r.get("error", ""))[:250]))
    return r


url = None
r = call(10, "homeContent", [""])
cats = jparse(r.get("result")).get("class") or []
print("分类:", [(c.get("type_id"), c.get("type_name")) for c in cats[:6]])
if cats:
    tid = cats[0].get("type_id")
    r = call(11, "categoryContent", [str(tid), "1", "false", ""])
    lst = jparse(r.get("result")).get("list") or []
    print("影片数:", len(lst))
    if lst:
        vid = lst[0].get("vod_id")
        print("首片:", vid, str(lst[0].get("vod_name"))[:30])
        r = call(12, "detailContent", [str(vid)])
        item = (jparse(r.get("result")).get("list") or [{}])[0]
        froms = [x for x in str(item.get("vod_play_from", "")).split("$$$") if x]
        urls = [x for x in str(item.get("vod_play_url", "")).split("$$$") if x]
        print("源:", froms[:4])
        if froms and urls:
            first = urls[0].split("#")[0]
            title, _, eid = first.partition("$")
            print("集名=%r id 长度=%d" % (title[:40], len(eid)))
            r = call(13, "playerContent", [froms[0], eid])
            pr = jparse(r.get("result"))
            url = pr.get("url")
            print("play url:", url)

if url:
    path = quote(urlparse(url).path, safe="/")
    probes = [
        (20, 6678, "/", "6678 根路径", 512),
        (21, 9978, "/", "9978 对照（本桥）", 256),
        (22, 6678, path, "6678 真实 play", 1024),
        (23, 6678, path, "6678 真实 play #2", 1024),
    ]
    for pid, port, p, note, mx in probes:
        rr, tt = rpc({"id": pid, "op": "fetch", "host": "127.0.0.1", "port": port,
                      "path": p, "max": mx})
        print("probe %s [%d]: %.3fs %s" % (note, port, tt, str(rr.get("result"))[:700]))
print("done")
