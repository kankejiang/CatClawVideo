/* dnsshim.c —— 没有 netd 的 guest 里，域名解析必须自己补一刀（与 proppreload.c 编进同一个 .so）。
 *
 * 四步实测定位（2026-09-25，同一台 QEMU、同一镜像）：
 *   ① /etc/resolv.conf 没用 —— Android 9 的 bionic 不读它（musl/busybox 才读）；
 *   ② net.dns1 属性也没用 —— 带垫片的 getprop 能看到值，解析照样 unknown host；
 *      bionic 只认 netd（/dev/socket/fwmarkd）给的服务器，没 netd 就等于零台服务器；
 *   ③ slirp 自带的 10.0.2.3:53 走 UDP 不通 —— 回 ICMP port-unreachable，
 *      errno 一路冒到 Java 变成 "android_getaddrinfo failed: ECONNREFUSED"；
 *   ④ 但 guest→宿主的 TCP 是通的（桥连接、jar 取回都走它）⇒ 解析交给宿主转发。
 *
 * 接管哪个符号（ELF 未定义符号表查的，不是猜的）：
 *   libjavacore.so（libcore 的 Linux.getaddrinfo，所有 Java 层解析的落点）只导入
 *   android_getaddrinfofornet；libcutils.so 导入 POSIX getaddrinfo。
 *   ⚠ /system/bin/ping 走的是另一族入口 —— 别拿 ping 验证这条路。
 *
 * ABI 以调用点反汇编为准（llvm-objdump -d libjavacore.so，调用点 0x2a800）：
 *   x0=node x1=service x2=&hints(栈上 4 个 int) w3=uid w4=netid x5=res
 *   ⇒ 6 个参数、res 在 x5。按"7 参数、res 在 x6"写会把结果写进垃圾寄存器，
 *   直接 SIGBUS(BUS_ADRALN) 打死整个 guest。
 *
 * 结果构造：拿到 IP 后**不自己 malloc addrinfo**（Android 9 的 StructAddrinfo 不认外部造的链，
 * 会抛 "Deprecated IPv4 address format: <域名>"），而是把点分十进制字面量再喂给真实现一次，
 * 让 bionic 走它自己验证过的构造路径。
 */
#include <arpa/inet.h>
#include <ctype.h>
#include <dlfcn.h>
#include <errno.h>
#include <netdb.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

extern int __system_property_get(const char *name, char *value);   /* 同 .so 里 proppreload.c 那份 */

typedef int (*gai_fn)(const char *, const char *, const struct addrinfo *, struct addrinfo **);
typedef int (*for_net_fn)(const char *, const char *, const struct addrinfo *,
                          unsigned, unsigned, struct addrinfo **);

static for_net_fn real_fornet;
static gai_fn real_gai;

/* 头三次无条件打一行：没这行分不清"钩子没生效"和"解析失败"。之后要 DNSHIM=1 才打。 */
static int dbg(void) {
    static int hits = 0;
    if (getenv("DNSHIM")) return 1;
    return hits++ < 3;
}

static struct in_addr dns_server(void) {
    char v[92] = {0};
    struct in_addr a;
    memset(&a, 0, sizeof a);
    if (__system_property_get("net.dns1", v) > 0 && inet_aton(v, &a)) return a;
    inet_aton("10.0.2.3", &a);
    return a;
}

/* 位置自检：只有看着像主机名才接管；顺带兜住位次猜错时读到野指针。 */
static int looks_like_host(const char *s) {
    if (!s) return 0;
    int n = 0;
    for (; n < 254; n++) {
        char c = s[n];
        if (c == 0) break;
        if (!(isalnum((unsigned char)c) || c == '.' || c == '-' || c == '_')) return 0;
    }
    return n > 0 && s[n] == 0;
}

/* AI_NUMERICHOST 是 libcore 在"这串是不是字面量 IP"时的探测调用（实测 hints.flags=4）。
 * 绝不能替它回答：一答，libcore 就认定域名是字面量，转手去 numericToBytes("raw.liucn.cc")
 * 抛 "Deprecated IPv4 address format"（2026-09-25 用 guest 内探针定位，见 p3/DnsProbe.java）。*/
static int numeric_probe(const struct addrinfo *hints) {
    return hints && (hints->ai_flags & AI_NUMERICHOST);
}

/* 真实现失败才算"该我们上"；成功就别抢。 */
static int want_us(int rc) {
    return rc == EAI_NONAME || rc == EAI_NODATA || rc == EAI_FAIL || rc == EAI_AGAIN
           || rc == EAI_SYSTEM || rc == EAI_ADDRFAMILY;
}

static int res_is_pointer(struct addrinfo **res) {
    unsigned long v = (unsigned long) (void *) res;
    return res && !(v & 7UL) && v > 0x1000UL;
}

/* ── 路线一（默认）：问宿主。两行文本协议 —— 两端都是自己人，不值得写 DNS 报文。 ──
 *   请求 "Q <host>\n"     应答 "A <ip> [<ip>…]\n" 或 "NX\n"
 * 端口经 kernel cmdline 的 ctrl= 传进来，/init 导出成 CATCLAW_DNS=10.0.2.2:<port>。 */
static int dns_via_host(const char *host, struct in_addr *out) {
    const char *ep = getenv("CATCLAW_DNS");
    if (!ep || !*ep) return 0;
    char buf[128];
    snprintf(buf, sizeof buf, "%s", ep);
    char *colon = strrchr(buf, ':');
    if (!colon) return 0;
    *colon = 0;
    struct sockaddr_in sa;
    memset(&sa, 0, sizeof sa);
    sa.sin_family = AF_INET;
    sa.sin_port = (unsigned short) htons(atoi(colon + 1));
    if (!inet_aton(buf, &sa.sin_addr)) return 0;

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return 0;
    struct timeval tv = { 5, 0 };
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    int ok = 0;
    char line[512];
    if (connect(fd, (struct sockaddr *) &sa, sizeof sa) == 0) {
        int ln = snprintf(line, sizeof line, "Q %s\n", host);
        if (send(fd, line, ln, MSG_NOSIGNAL) == ln) {
            int got = (int) recv(fd, line, sizeof line - 1, 0);
            if (got > 2 && line[0] == 'A' && line[1] == ' ') {
                line[got] = 0;
                char ip[64];
                int i = 0, j = 2;
                while (line[j] && line[j] != ' ' && line[j] != '\r' && line[j] != '\n' && i < 63)
                    ip[i++] = line[j++];
                ip[i] = 0;
                ok = inet_aton(ip, out) ? 1 : 0;
            }
        }
    } else if (dbg()) {
        fprintf(stderr, "[dnsshim] 连宿主 DNS 转发失败 errno=%d\n", errno);
    }
    close(fd);
    return ok;
}

/* ── 路线二（没给 CATCLAW_DNS 时的老路，实测会被 slirp 拒绝）：自己发一个 IN/A 查询 ── */
static int put_name(unsigned char *p, const char *host, int cap) {
    int n = 0;
    const char *s = host;
    while (*s) {
        const char *dot = strchr(s, '.');
        int len = dot ? (int) (dot - s) : (int) strlen(s);
        if (len <= 0 || len > 63 || n + 1 + len > cap) return -1;
        p[n++] = (unsigned char) len;
        memcpy(p + n, s, len);
        n += len;
        if (!dot) break;
        s = dot + 1;
    }
    if (n + 1 > cap) return -1;
    p[n++] = 0;
    return n;
}

/* 跳过一个 name（压缩指针只跳 2 字节，其后紧跟固定字段） */
static int skip_name(const unsigned char *r, int len, int off) {
    while (off < len) {
        int l = r[off];
        if (l == 0) return off + 1;
        if ((l & 0xC0) == 0xC0) return off + 2;
        off += 1 + l;
    }
    return -1;
}

static unsigned short qid(void) {
    static unsigned seed = 0;
    seed = seed * 1103515245u + (unsigned) getpid() + 12345u;
    return (unsigned short) (seed >> 7);
}

static int dns_udp(const char *host, struct in_addr *out) {
    unsigned char q[300], r[700];
    memset(q, 0, sizeof q);
    unsigned short id = qid();
    q[0] = (unsigned char) (id >> 8); q[1] = (unsigned char) (id & 0xff);
    q[2] = 0x01;                          /* 递归期望 */
    q[5] = 0x01;                          /* QDCOUNT = 1 */
    int n = 12;
    int nn = put_name(q + n, host, (int) sizeof q - n - 4);
    if (nn < 0) return 0;
    n += nn;
    q[n++] = 0; q[n++] = 1;               /* QTYPE = A */
    q[n++] = 0; q[n++] = 1;               /* QCLASS = IN */

    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) return 0;
    struct timeval tv = { 3, 0 };
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    struct sockaddr_in to;
    memset(&to, 0, sizeof to);
    to.sin_family = AF_INET;
    to.sin_port = htons(53);
    to.sin_addr = dns_server();
    if (sendto(fd, q, n, 0, (struct sockaddr *) &to, sizeof to) < 0) { close(fd); return 0; }
    int got = (int) recv(fd, r, sizeof r, 0);
    close(fd);
    if (got < 12 || (r[2] & 0x0f) != 0) return 0;
    int ancount = (r[6] << 8) | r[7];
    int off = skip_name(r, got, 12);
    if (off < 0 || off + 4 > got) return 0;
    off += 4;                             /* QTYPE + QCLASS */
    for (int i = 0; i < ancount; i++) {
        off = skip_name(r, got, off);
        if (off < 0 || off + 10 > got) break;
        int type = (r[off] << 8) | r[off + 1];
        int rdlen = (r[off + 8] << 8) | r[off + 9];
        off += 10;
        if (type == 1 && rdlen == 4 && off + 4 <= got) {
            memcpy(&out->s_addr, r + off, 4);
            return 1;
        }
        off += rdlen;
    }
    return 0;
}

static int dns_lookup_one(const char *host, struct in_addr *out) {
    return dns_via_host(host, out) ? 0 : (dns_udp(host, out) ? 0 : -1);
}

/* 名字换成 IP 字面量后交给真实现构造 addrinfo —— 我们绝不自己拼那块内存。 */
static int retry_with_literal(const char *node, const char *service, const struct addrinfo *hints,
                              struct addrinfo **res, for_net_fn fn6, gai_fn fn4) {
    struct in_addr a;
    if (dns_lookup_one(node, &a) != 0) return -1;
    char num[64];
    if (!inet_ntop(AF_INET, &a, num, sizeof num)) return -1;
    /* 关键：re-entry 的 hints 必须**换成具体形状**。照抄调用方的 hints（fam=0/st=0/proto=0）
     * 会让 bionic 一次返回两条（DGRAM/UDP + STREAM/TCP），libcore 的
     * StructAddrinfo→InetAddress 转换就会抛 "Deprecated IPv4 address format: <域名>"。
     * AF_INET + SOCK_STREAM + IPPROTO_TCP 是设备上任何字面量查询天天在走的路径，肯定安全。 */
    struct addrinfo h;
    memset(&h, 0, sizeof h);
    h.ai_family = AF_INET;
    h.ai_socktype = SOCK_STREAM;
    h.ai_protocol = IPPROTO_TCP;
    int rc2 = fn6 ? fn6(num, service, &h, 0, 0, res) : (fn4 ? fn4(num, service, &h, res) : -1);
    if (dbg()) {
        fprintf(stderr, "[dnsshim] %s → %s，交给 bionic 构造 rc=%d (hints fam=%d st=%d proto=%d flags=%x)\n",
                node, num, rc2, hints ? hints->ai_family : -1, hints ? hints->ai_socktype : -1,
                hints ? hints->ai_protocol : -1, hints ? hints->ai_flags : 0);
        if (rc2 == 0 && res_is_pointer(res) && *res) {
            int k = 0;
            for (struct addrinfo *p = *res; p && k < 4; p = p->ai_next, k++)
                fprintf(stderr, "[dnsshim]   答案[%d] fam=%d st=%d proto=%d addrlen=%u canon=%s\n",
                        k, p->ai_family, p->ai_socktype, p->ai_protocol,
                        (unsigned) p->ai_addrlen, p->ai_canonname ? p->ai_canonname : "-");
        }
    }
    return rc2;
}

int android_getaddrinfofornet(const char *node, const char *service, const struct addrinfo *hints,
                              unsigned uid, unsigned egress_netid, struct addrinfo **res) {
    if (!real_fornet) real_fornet = (for_net_fn) dlsym(RTLD_NEXT, "android_getaddrinfofornet");
    if (!real_fornet) return EAI_FAIL;
    int rc = real_fornet(node, service, hints, uid, egress_netid, res);
    if ((rc == 0 && res_is_pointer(res) && *res) || !want_us(rc) || !looks_like_host(node)
        || numeric_probe(hints)) return rc;
    if (!res_is_pointer(res)) {
        if (dbg()) fprintf(stderr, "[dnsshim] res 不像指针(%p)，不接管 %s\n", (void *) res, node);
        return rc;
    }
    return retry_with_literal(node, service, hints, res, real_fornet, NULL) == 0 ? 0 : rc;
}

int getaddrinfo(const char *node, const char *service, const struct addrinfo *hints, struct addrinfo **res) {
    if (!real_gai) real_gai = (gai_fn) dlsym(RTLD_NEXT, "getaddrinfo");
    if (!real_gai) return EAI_FAIL;
    int rc = real_gai(node, service, hints, res);
    if ((rc == 0 && res_is_pointer(res) && *res) || !want_us(rc) || !looks_like_host(node)
        || numeric_probe(hints)) return rc;
    if (!res_is_pointer(res)) return rc;
    return retry_with_literal(node, service, hints, res, NULL, real_gai) == 0 ? 0 : rc;
}
