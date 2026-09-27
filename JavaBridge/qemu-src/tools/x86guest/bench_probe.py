#!/usr/bin/env python3
"""6678 壳流服务探针（x86 guest）：load 玩偶 → home/category/detail → playerContent
→ 从 guest 内用原始 socket 直连 6678 看「到底回了什么」。

2026-09-27 播放失败排查背景：宿主播放器 → 18602(hostfwd) → guest 9978(Art 桥)
→ 透传 127.0.0.1:6678（壳的流服务）报 unexpected end of stream。本脚本用新加的
fetch op 在 guest 内直接取证：6678 上是有服务还是空端口、秒断还是回非 HTTP。
"""
import os, socket, sys, json, time
from urllib.parse import quote, urlparse

# 用法：python bench_probe.py [桥端口=18600] [玩偶 jar 路径或 URL=inbox 默认]
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18600
JAR = sys.argv[2] if len(sys.argv) > 2 else "/data/catclaw/art/inbox/raw-08a27c1fff2c064ae9a12c34.jar"

s = socket.create_connection(("127.0.0.1", PORT), timeout=15)
f = s.makefile("rwb")


def rpc(obj, timeout=300):
    f.write((json.dumps(obj) + "\n").encode()); f.flush()
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


_, t = rpc({"id": 1, "op": "ping"})
print("ping: %.3fs" % t)

r, _ = rpc({"id": 2, "op": "netstat"})
print("netstat: %r" % str(r.get("result"))[:300])

# 回灌 prefs（与宿主 RestoreGuestPrefsAsync 同语义）：壳 init 读 spUtils（夸克 Cookie 等），
# 108 之前从不回灌 → 壳读缺省后崩（用户环境有回灌所以全链路活）。必须在 load 之前。
pdir = "/root/x86guest/guest-prefs"
if os.path.isdir(pdir):
    mid = 100
    for fn in sorted(os.listdir(pdir)):
        if not fn.endswith(".xml"):
            continue
        xml = open(os.path.join(pdir, fn), encoding="utf-8").read()
        pr, _ = rpc({"id": mid, "op": "prefsput", "name": fn[:-4], "xml": xml})
        print("prefsput %s (%dB): ok=%s" % (fn, len(xml), pr.get("ok")))
        mid += 1
else:
    print("注意：无 guest-prefs 目录，跳过回灌（壳可能因缺 Cookie 崩）")

r, t = rpc({"id": 3, "op": "load", "site": "wogg", "className": "WoGGGuard", "ext": "",
            "jars": [JAR]}, timeout=300)
print("load: %.3fs ok=%s %s" % (t, r.get("ok"), str(r.get("error", ""))[:300]))
if not r.get("ok"):
    raise SystemExit(1)


def call(mid, method, args, timeout=300):
    r, t = rpc({"id": mid, "op": "call", "site": "wogg", "method": method, "args": args}, timeout)
    print("%s: %.3fs ok=%s %s" % (method, t, r.get("ok"), str(r.get("error", ""))[:250]))
    return r


r = call(10, "homeContent", [""])
raw10 = str(r.get("result", ""))
cats = jparse(r.get("result")).get("class") or []
print("分类:", [(c.get("type_id"), c.get("type_name")) for c in cats[:5]])
if not cats:
    print("homeContent raw head:", raw10[:900])
    raise SystemExit(1)
tid = cats[0].get("type_id")

r = call(11, "categoryContent", [str(tid), "1", "false", ""])
lst = jparse(r.get("result")).get("list") or []
print("影片数:", len(lst))
if not lst:
    raise SystemExit(1)
vid = lst[0].get("vod_id")
print("首片:", vid, str(lst[0].get("vod_name"))[:30])

r = call(12, "detailContent", [str(vid)])
lst2 = jparse(r.get("result")).get("list") or []
if not lst2:
    raise SystemExit(1)
item = lst2[0]
froms = str(item.get("vod_play_from", "")).split("$$$")
urls = str(item.get("vod_play_url", "")).split("$$$")
print("源:", froms[:6])
if not froms or not urls or not urls[0]:
    raise SystemExit(1)
flag = froms[0]
first = urls[0].split("#")[0]
print("首集条目:", first[:180])
if "$" not in first:
    raise SystemExit(1)
title, _, eid = first.partition("$")
print("flag=%r 集名=%r id 长度=%d" % (flag, title[:40], len(eid)))

r = call(13, "playerContent", [flag, eid])
pr = jparse(r.get("result"))
url = pr.get("url")
print("play url:", url)
if not url:
    raise SystemExit(1)

u = urlparse(url)
path = quote(u.path, safe="/")


def probe(pid, port, p, note, mx=1024):
    r, t = rpc({"id": pid, "op": "fetch", "host": "127.0.0.1", "port": port,
                "path": p, "max": mx})
    print("probe %s [%d%s]: %.3fs %s" % (note, port, p[:60], t, str(r.get("result"))[:700]))
    return r


probe(20, 6678, "/", "6678 根路径")
probe(21, 9978, "/", "9978 对照（我们的服务）")
probe(22, 6678, path, "6678 真实 play 路径")
probe(23, 6678, path, "6678 真实 play 路径 #2")
print("done")
