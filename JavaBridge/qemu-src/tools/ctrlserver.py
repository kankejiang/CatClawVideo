#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
宿主侧控制服务器（对应 guest 里 ctrlloop 的轮询：GET http://10.0.2.2:<port>/task）

产品里这份逻辑是 QemuControlServer.cs（C#）；本脚本用于**实验**——
按脚本给出一串命令，每条保持 hold 秒后被下一条替换；列表用完后一直重复最后一条。

用法:
  python ctrlserver.py <port> [--hold 8] "PROBE" "TASK MAGNET magnet:?xt=urn:btih:XXX name.mp4"

会打印 guest 发来的每个请求（含查询串）——那是观察任务进度的窗口。
"""
import sys
import time
import socket
from datetime import datetime

args = [a for a in sys.argv[1:]]
if not args:
    print(__doc__)
    sys.exit(1)

port = int(args.pop(0))
hold = 8
if "--hold" in args:
    i = args.index("--hold")
    hold = int(args[i + 1])
    del args[i:i + 2]
CMDS = args or [""]

srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("127.0.0.1", port))
srv.listen(16)
print("[ctrl] 监听 127.0.0.1:%d  hold=%ds  命令数=%d" % (port, hold, len(CMDS)), flush=True)

t0 = time.time()
last_log = 0.0
while True:
    try:
        conn, addr = srv.accept()
    except KeyboardInterrupt:
        break
    conn.settimeout(3)
    try:
        req = b""
        while b"\r\n\r\n" not in req and len(req) < 8192:
            b = conn.recv(4096)
            if not b:
                break
            req += b
        line = req.split(b"\r\n", 1)[0].decode("latin1", "replace")
        idx = int((time.time() - t0) / hold)
        idx = min(idx, len(CMDS) - 1)
        body = CMDS[idx].encode("utf-8")
        # 只在 (a) 非 /task 的请求 或 (b) 命令刚切换时打一行，避免每秒刷屏
        if "/task" not in line or idx != last_log:
            print("[ctrl] %s  %s   → 回: %r" % (datetime.now().strftime("%H:%M:%S"), line[:110],
                                               body.decode("utf-8", "replace")[:80]), flush=True)
            last_log = idx
        hdr = ("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: %d\r\n"
               "Connection: close\r\n\r\n" % len(body)).encode("ascii")
        conn.sendall(hdr + body)
    except Exception as e:
        print("[ctrl] 处理请求异常: %s" % e, flush=True)
    finally:
        try:
            conn.close()
        except Exception:
            pass
