#!/usr/bin/env python3
"""108 侧宿主 DNS 转发（ConteClawVideo.Core/Services/QemuGuest/ArtDnsServer.cs 的 python 等价物）。

背景（2026-09-27 定位）：guest 的 dnsshim 解析路线一是 `CATCLAW_DNS=10.0.2.2:<ctrl端口>`
（cmdline `ctrl=` → /init 导出），TCP 文本协议：收 "Q <host>\n"，回 "A <ip> [<ip>…]\n" 或 "NX\n"。
用户机上是宿主 C# 的 ArtDnsServer 在听；108 没有宿主应用 → 必须本脚本补位，否则 guest 解析
全挂（壳 UnknownHostException → homeContent 秒返空）。

slirp 的 10.0.2.2 就是「宿主 loopback」：guest 连 10.0.2.2:11729 直达本机 127.0.0.1:11729，
无需 hostfwd。用本机 resolver（/etc/resolv.conf）解析。
"""
import socket, sys, threading

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 11729


def serve(c):
    try:
        req = c.recv(256).decode("ascii", "replace").strip()
        if not req.startswith("Q "):
            return
        host = req[2:].split()[0]
        try:
            infos = socket.getaddrinfo(host, None, socket.AF_INET)
            ips = []
            for it in infos:
                ip = it[4][0]
                if ip not in ips:
                    ips.append(ip)
            ans = ("A " + " ".join(ips) + "\n") if ips else "NX\n"
        except Exception:
            ans = "NX\n"
        c.sendall(ans.encode("ascii"))
    except Exception:
        pass
    finally:
        try:
            c.close()
        except Exception:
            pass


def main():
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", PORT))
    srv.listen(64)
    print("[dnsfwd] listening 127.0.0.1:%d" % PORT, flush=True)
    while True:
        c, _ = srv.accept()
        threading.Thread(target=serve, args=(c,), daemon=True).start()


if __name__ == "__main__":
    main()
