#!/usr/bin/env python3
# Guard 壳 jar 转译验收（x86 guest）：load WoGGGuard -> homeContent
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

resp, t = rpc({"id":2, "op":"load", "site":"wogg", "className":"WoGGGuard",
               "ext":"", "jars":["/data/catclaw/art/inbox/raw-08a27c1fff2c064ae9a12c34.jar"]}, timeout=300)
print("load(WoGGGuard): %.3fs ok=%s" % (t, resp.get("ok")))
if not resp.get("ok"):
    print("load error:", str(resp.get("error",""))[:2500])
else:
    resp2, t2 = rpc({"id":3, "op":"call", "site":"wogg", "method":"homeContent", "args":[""]}, timeout=300)
    print("homeContent: %.3fs ok=%s" % (t2, resp2.get("ok")), str(resp2.get("error",""))[:300])
    print("result head:", str(resp2.get("result",""))[:400])
