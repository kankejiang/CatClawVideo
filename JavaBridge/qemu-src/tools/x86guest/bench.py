#!/usr/bin/env python3
# 桥基准：ping -> load(tvbox.apk dex) 计时
import socket, json, time
s = socket.create_connection(("127.0.0.1", 18600), timeout=10)
f = s.makefile("rwb")
def rpc(obj, timeout=120):
    f.write((json.dumps(obj) + "\n").encode()); f.flush()
    t0 = time.time()
    while True:
        line = f.readline()
        if not line: raise RuntimeError("EOF")
        resp = json.loads(line)
        if resp.get("id") == obj["id"]:
            return resp, time.time() - t0
_, t = rpc({"id":1, "op":"ping"})
print("ping: %.3fs -> pong" % t)
resp, t = rpc({"id":2, "op":"load", "site":"bench", "className":"com.github.catvod.spider.XPath",
               "ext":"", "jars":["/tvbox.apk"]}, timeout=180)
print("load(tvbox.apk dex): %.3fs ok=%s" % (t, resp.get("ok")), str(resp.get("error", ""))[:120])