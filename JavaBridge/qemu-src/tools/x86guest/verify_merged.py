#!/usr/bin/env python3
"""合并 guest 收尾验证（在 108 上跑）：
1) 桥 ping（宿主 18602 → hostfwd → guest 桥）
2) 起 18080 监听，等 harness 回连（证明引擎在 guest 里活着并按协议找宿主）
"""
import socket

# 1) 桥 ping
try:
    s = socket.create_connection(("127.0.0.1", 18602), timeout=8)
    s.sendall(b'{"id":1,"op":"ping"}\n')
    s.settimeout(8)
    print("bridge ping:", s.recv(200).decode("utf-8", "replace").strip()[:120])
    s.close()
except Exception as e:
    print("bridge:", e)

# 2) 监听 18080 等 harness 回连
srv = socket.socket()
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("127.0.0.1", 18080))
srv.listen(4)
srv.settimeout(120)
print("18080 listening (等 harness 回连，最多 120s；harness 失败后可能有退避，耐心等)...")
try:
    c, a = srv.accept()
    print("harness 回连! 来自", a)
    c.settimeout(8)
    data = c.recv(512)
    print("首包:", data[:220])
    c.close()
except Exception as e:
    print("30s 内无回连:", e)
srv.close()
