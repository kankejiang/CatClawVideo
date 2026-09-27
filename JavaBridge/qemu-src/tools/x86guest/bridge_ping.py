#!/usr/bin/env python3
"""合并 guest 桥 ping（108 上跑）：连 hostfwd 18602 发一行 ping 请求，打印应答。"""
import socket

try:
    s = socket.create_connection(("127.0.0.1", 18602), timeout=8)
    s.sendall(b'{"id":1,"op":"ping"}\n')
    s.settimeout(8)
    print("桥 ping 应答:", s.recv(256).decode("utf-8", "replace").strip()[:200])
    s.close()
except Exception as e:
    print("桥不可达:", e)
