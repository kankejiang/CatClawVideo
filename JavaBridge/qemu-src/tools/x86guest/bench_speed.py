#!/usr/bin/env python3
# 非 Guard 源原生速度验收：load DouDou -> homeContent -> categoryContent -> detailContent 计时
# 对照：aarch64 TCG 上聚合网盘源 detailContent 71~90s（2026-09 实测）
import socket, json, time

s = socket.create_connection(("127.0.0.1", 18600), timeout=15)
f = s.makefile("rwb")

def rpc(obj, timeout=300):
    f.write((json.dumps(obj) + "\n").encode()); f.flush()
    t0 = time.time()
    while True:
        line = f.readline()
        if not line: raise RuntimeError("EOF")
        resp = json.loads(line)
        if resp.get("id") == obj["id"]:
            return resp, time.time() - t0

_, t = rpc({"id":1, "op":"ping"})
print("ping: %.3fs" % t)

resp, t = rpc({"id":2, "op":"load", "site":"bench", "className":"Bili",
               "ext":"", "jars":["/fty.jar"]}, timeout=180)
print("load(Bili): %.3fs ok=%s %s" % (t, resp.get("ok"), str(resp.get("error",""))[:150]))

if resp.get("ok"):
    resp, t = rpc({"id":3, "op":"call", "site":"bench", "method":"homeContent", "args":[""]}, timeout=180)
    ok = resp.get("ok")
    head = str(resp.get("result",""))[:150].replace("\n", " ")
    print("homeContent: %.3fs ok=%s | %s" % (t, ok, head))
    if ok:
        r = json.loads(resp.get("result") or "{}") if isinstance(resp.get("result"), str) else (resp.get("result") or {})
        cls = r.get("class") or []
        if cls:
            tid = cls[0].get("type_id")
            resp, t = rpc({"id":4, "op":"call", "site":"bench", "method":"categoryContent",
                           "args":[str(tid), "1", "false", ""]}, timeout=180)
            ok4 = resp.get("ok")
            r4 = json.loads(resp.get("result") or "{}") if isinstance(resp.get("result"), str) else (resp.get("result") or {})
            movies = r4.get("list") or []
            vid = (movies[0].get("vod_id") if movies else None)
            print("categoryContent: %.3fs ok=%s 影片数=%d" % (t, ok4, len(movies)))
            if vid is not None:
                resp, t = rpc({"id":5, "op":"call", "site":"bench", "method":"detailContent",
                               "args":[str(vid)]}, timeout=180)
                print("detailContent: %.3fs ok=%s（aarch64 TCG 对照 71~90s）" % (t, resp.get("ok")))
