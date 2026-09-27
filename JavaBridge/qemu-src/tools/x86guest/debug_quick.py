#!/usr/bin/env python3
"""对运行中桥发一条 fetch/op 并打印结果（全 ASCII 输出，避免控制台编码干扰）。

用法：python debug_quick.py <port> <path> [max]     # fetch 探针
     python debug_quick.py --json '<完整 op JSON>'  # 任意 op（call 等）
"""
import json
import os
import sys
import time

BASE = os.path.join(os.environ["APPDATA"], "CatClawVideo.debug", "javabridge")
IN = os.path.join(BASE, "bridge-debug-in.jsonl")
OUT = os.path.join(BASE, "bridge-debug-out.log")

if sys.argv[1] == "--json":
    obj = json.loads(sys.argv[2])
    obj["id"] = int(time.time()) % 100000 + 3000
else:
    port = int(sys.argv[1])
    path = sys.argv[2]
    mx = int(sys.argv[3]) if len(sys.argv) > 3 else 512
    obj = {"id": int(time.time()) % 100000 + 3000, "op": "fetch", "host": "127.0.0.1",
           "port": port, "path": path, "max": mx}

line = json.dumps(obj, ensure_ascii=False)
with open(IN, "a", encoding="utf-8") as f:
    f.write(line + "\n")
print(">> sent id=%s op=%s" % (obj["id"], obj.get("op")))

t0 = time.time()
while time.time() - t0 < 150:
    time.sleep(1)
    if os.path.exists(OUT):
        txt = open(OUT, encoding="utf-8", errors="replace").read()
        i = txt.rfind("<- " + line)
        if i >= 0:
            seg = txt[i:].split("\n")
            if len(seg) >= 2 and seg[1].strip():
                print(json.dumps(seg[1][:800], ensure_ascii=True))
                sys.exit(0)
print("timeout")
