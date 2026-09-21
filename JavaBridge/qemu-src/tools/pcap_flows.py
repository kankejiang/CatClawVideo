#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 pcap 里的 TCP 流按四元组重组成文本，看明文 HTTP 的请求与响应全文。"""
import struct
import sys
from collections import defaultdict

PCAP = sys.argv[1] if len(sys.argv) > 1 else r"C:\Code\.unidbg-probe\engine.pcap"
MAX = int(sys.argv[2]) if len(sys.argv) > 2 else 1500

d = open(PCAP, "rb").read()
endian = "<" if d[:4] == b"\xd4\xc3\xb2\xa1" else ">"
linktype = struct.unpack_from(endian + "I", d, 20)[0]
off, pkts = 24, []
while off + 16 <= len(d):
    _, _, incl, _ = struct.unpack_from(endian + "IIII", d, off)
    off += 16
    if incl <= 0 or off + incl > len(d):
        break
    pkts.append(d[off:off + incl]); off += incl


def ip2s(b):
    return "%d.%d.%d.%d" % tuple(b)


flows = defaultdict(lambda: {"c2s": bytearray(), "s2c": bytearray()})
for p in pkts:
    if linktype == 1:
        if len(p) < 14 or struct.unpack_from(">H", p, 12)[0] != 0x0800:
            continue
        ip = p[14:]
    else:
        ip = p
    if len(ip) < 20 or ip[9] != 6:
        continue
    ihl = (ip[0] & 0x0F) * 4
    tot = struct.unpack_from(">H", ip, 2)[0]
    src, dst = ip2s(ip[12:16]), ip2s(ip[16:20])
    t = ip[ihl:tot]
    if len(t) < 20:
        continue
    sport, dport = struct.unpack_from(">HH", t, 0)
    off_t = (t[12] >> 4) * 4
    payload = t[off_t:]
    if not payload:
        continue
    if dst.startswith("10.0.2.") and not dst == "10.0.2.3":
        continue
    if src.startswith("10.0.2."):
        key = (src, sport, dst, dport)
        flows[key]["c2s"] += payload
    else:
        key = (dst, dport, src, sport)
        flows[key]["s2c"] += payload

print("=" * 70)
print(" TCP 明文流内容（端口 80 为主）")
print("=" * 70)
for (a, ap, b, bp), v in sorted(flows.items(), key=lambda x: -len(x[1]["c2s"])):
    if bp not in (80, 8080) and ap not in (80, 8080):
        continue
    print("\n──── %s:%d ⇄ %s:%d   （上行 %d 字节 / 下行 %d 字节）────"
          % (a, ap, b, bp, len(v["c2s"]), len(v["s2c"])))
    for tag, buf in (("→", v["c2s"]), ("←", v["s2c"])):
        s = bytes(buf[:MAX]).decode("latin1")
        s = "".join(c if 32 <= ord(c) < 127 or c in "\r\n\t" else "." for c in s)
        if s.strip():
            print("  %s " % tag + s.replace("\r\n", "\n     ").strip()[:MAX])
