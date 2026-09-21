#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
解析 QEMU filter-dump 落下的 pcap（Ethernet / IPv4 / TCP+UDP），
把「引擎到底在跟谁说话、说什么」摊开：
  - TCP 目的端点 + SYN 次数
  - TLS ClientHello 里的 SNI（HTTPS 也能拿到主机名）
  - 明文 HTTP 的请求行与 Host 头
  - UDP 目的端点（含 DNS 查询名）
"""
import struct
import sys
from collections import Counter, defaultdict

PCAP = sys.argv[1] if len(sys.argv) > 1 else r"C:\Code\.unidbg-probe\engine.pcap"


def read_pcap(path):
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 24:
        return [], 1
    magic = data[:4]
    # 魔数即「字节序标记」：文件里是 d4c3b2a1 ⇒ 写入方是小端，后续字段按小端读
    if magic == b"\xd4\xc3\xb2\xa1":
        endian = "<"
    elif magic == b"\xa1\xb2\xc3\xd4":
        endian = ">"
    elif magic == b"\x4d\x3c\xb2\xa1":          # pcapng
        return None, None
    else:
        return [], 1
    linktype = struct.unpack_from(endian + "I", data, 20)[0]
    off, out = 24, []
    while off + 16 <= len(data):
        _, _, incl, _ = struct.unpack_from(endian + "IIII", data, off)
        off += 16
        if incl <= 0 or off + incl > len(data):
            break
        out.append(data[off:off + incl])
        off += incl
    return out, linktype


def ip2s(b):
    return "%d.%d.%d.%d" % (b[0], b[1], b[2], b[3])


def parse_sni(p):
    """从 TLS ClientHello 里取 SNI"""
    try:
        if len(p) < 6 or p[0] != 0x16 or p[1] != 0x03:
            return None
        rl = struct.unpack_from(">H", p, 3)[0]
        i = 5
        if p[i] != 0x01:
            return None
        hs = struct.unpack_from(">I", p, i + 1)[0] & 0xFFFFFF
        i += 4 + 2 + 32                      # handshake hdr + version + random
        sid = p[i]
        i += 1 + sid
        cs = struct.unpack_from(">H", p, i)[0]
        i += 2 + cs
        cm = p[i]
        i += 1 + cm
        ext_total = struct.unpack_from(">H", p, i)[0]
        i += 2
        end = min(i + ext_total, len(p))
        while i + 4 <= end:
            et, el = struct.unpack_from(">HH", p, i)
            i += 4
            if et == 0x0000 and el >= 5:     # server_name
                ln = struct.unpack_from(">H", p, i + 3)[0]
                return p[i + 5:i + 5 + ln].decode("ascii", "replace")
            i += el
    except Exception:
        pass
    return None


def parse_dns_q(p, base):
    try:
        i = base + 12
        names = []
        while i < len(p) and p[i]:
            ln = p[i]
            names.append(p[i + 1:i + 1 + ln].decode("ascii", "replace"))
            i += 1 + ln
        if names:
            return ".".join(names)
    except Exception:
        pass
    return None


pkts, linktype = read_pcap(PCAP)
if pkts is None:
    print("  这是 pcapng 格式，本脚本只吃经典 pcap")
    sys.exit(1)
if not pkts:
    print("  ❌ pcap 为空或格式不识")
    sys.exit(1)

tcp_dst = Counter()
tcp_syn = Counter()
udp_dst = Counter()
sni = Counter()
http_host = Counter()
http_req = Counter()
dns_q = Counter()
bytes_out = Counter()
first_ts = None

for p in pkts:
    if linktype == 1:
        if len(p) < 14:
            continue
        et = struct.unpack_from(">H", p, 12)[0]
        if et != 0x0800:
            continue
        ip = p[14:]
    else:
        ip = p
    if len(ip) < 20:
        continue
    ihl = (ip[0] & 0x0F) * 4
    proto = ip[9]
    srcip, dstip = ip2s(ip[12:16]), ip2s(ip[16:20])
    if not (srcip == "10.0.2.15" or dstip == "10.0.2.15"):
        continue
    tot = struct.unpack_from(">H", ip, 2)[0]
    payload = ip[ihl:tot] if tot >= ihl else ip[ihl:]

    if proto == 6 and len(payload) >= 20:
        sport, dport = struct.unpack_from(">HH", payload, 0)
        doff = (payload[12] >> 4) * 4
        flags = payload[13]
        app = payload[doff:]
        # 记录「guest 主动外连」的方向
        if not dstip.startswith("10.0.2."):
            key = "%s:%d" % (dstip, dport)
            tcp_dst[key] += 1
            bytes_out[key] += tot
            if flags & 0x02:
                tcp_syn[key] += 1
            s = parse_sni(app)
            if s:
                sni[s] += 1
            if app[:1].isalpha():
                try:
                    txt = app.decode("latin1")
                    if "\r\n" in txt:
                        line = txt.split("\r\n", 1)[0]
                    else:
                        line = txt[:120]
                    if any(line.startswith(m) for m in
                           ("GET ", "POST ", "HEAD ", "PUT ", "CONNECT ", "OPTIONS ")):
                        http_req[line[:160]] += 1
                        for h in txt.split("\r\n"):
                            if h.lower().startswith("host:"):
                                http_host[h.split(":", 1)[1].strip()] += 1
                                break
                except Exception:
                    pass
    elif proto == 17 and len(payload) >= 8:
        sport, dport = struct.unpack_from(">HH", payload, 0)
        if not dstip.startswith("10.0.2.") or dstip == "10.0.2.3":
            udp_dst["%s:%d" % (dstip, dport)] += 1
        if dport == 53:
            q = parse_dns_q(payload, 8)
            if q:
                dns_q[q] += 1


def show(title, c, n=20, fmt=None):
    print("\n" + title)
    if not c:
        print("   （无）")
        return
    for k, v in c.most_common(n):
        print("   " + (fmt(k, v) if fmt else "%8d  %s" % (v, k)))


print("=" * 68)
print(" 抓包分析：%s" % PCAP)
print(" 报文总数（含收发）：%d" % len(pkts))
show("── TCP 外连端点（guest → 外网）──", tcp_dst,
     fmt=lambda k, v: "%6d 包  %-24s SYN×%d" % (v, k, tcp_syn.get(k, 0)))
show("── TLS SNI（HTTPS 也能看到主机名）──", sni)
show("── 明文 HTTP 请求行 ──", http_req, n=25)
show("── 明文 HTTP Host ──", http_host, n=25)
show("── UDP 外连端点（含 DNS）──", udp_dst)
show("── DNS 查询名 ──", dns_q, n=25)
print()
