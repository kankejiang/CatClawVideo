// ═══════════════════════════════════════════════════════════════════════
//  DNS 拦截层（被 harness4.c 以 #include 方式并入同一个编译单元）
//
//  为什么需要：
//    bionic 的 getaddrinfo 在**无 netd** 的环境里完全失效 —— 它走
//    libnetd_client → /dev/socket/dnsproxyd（netd 的 DNS 代理），而我们的
//    initramfs 里没有 netd；同时它**不读** /etc/resolv.conf。
//    实测：裸 IP 的 TCP connect 成功，但 getaddrinfo("www.baidu.com") 返回
//    EAI_NODATA(7) ⇒ 迅雷引擎连域名都解析不了，一个 socket 都建不起来。
//
//  做法：
//    在本可执行文件里**重新定义** getaddrinfo / android_getaddrinfo /
//    gethostbyname / gethostbyname2。bionic 的 linker 解析 dlopen 进来的
//    共享库的未定义符号时，会先搜主可执行文件所在全局组 ⇒ 引擎的调用会落到
//    我们这里（需以 -rdynamic 链接导出符号）。
//    解析策略：① 已是 IP 直接用 ② /etc/hosts ③ 自己发 UDP DNS 查询问
//    resolv.conf 里的 nameserver（qemu 用户网络下是 10.0.2.3）。
//    同时**打印引擎请求的每一个域名** —— 这对排障极有价值。
// ═══════════════════════════════════════════════════════════════════════

#include <netdb.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#include <errno.h>
#include <string.h>
#include <stdlib.h>
#include <stdio.h>

static char g_dns_server[64] = "";

// 从 /etc/resolv.conf 抓第一个 nameserver；抓不到就用 qemu 用户网络的 10.0.2.3
static const char *dns_server(void) {
    if (g_dns_server[0]) return g_dns_server;
    FILE *f = fopen("/etc/resolv.conf", "r");
    if (f) {
        char line[256];
        while (fgets(line, sizeof(line), f)) {
            char *p = strstr(line, "nameserver");
            if (!p) continue;
            p += 10;
            while (*p == ' ' || *p == '\t') p++;
            int i = 0;
            while (p[i] && p[i] != '\n' && p[i] != '\r' && i < 63) { g_dns_server[i] = p[i]; i++; }
            g_dns_server[i] = 0;
            break;
        }
        fclose(f);
    }
    if (!g_dns_server[0]) snprintf(g_dns_server, sizeof(g_dns_server), "10.0.2.3");
    printf("[dns-hook] nameserver = %s\n", g_dns_server);
    return g_dns_server;
}

// /etc/hosts 查表（严格匹配，支持 1 个别名）
static int hosts_lookup(const char *host, struct in_addr *out) {
    FILE *f = fopen("/etc/hosts", "r");
    if (!f) return -1;
    char line[512];
    int found = -1;
    while (fgets(line, sizeof(line), f)) {
        char *h = strchr(line, '#');
        if (h) *h = 0;
        char ip[64], name[256];
        if (sscanf(line, "%63s %255s", ip, name) != 2) continue;
        if (strcasecmp(name, host) != 0) continue;
        if (inet_pton(AF_INET, ip, out) == 1) { found = 0; break; }
    }
    fclose(f);
    return found;
}

// 最小 DNS 客户端：A 记录查询
static int dns_query_a(const char *host, struct in_addr *out) {
    unsigned char q[512];
    int n = 0;
    static unsigned short qid = 0x4321;
    qid++;
    q[n++] = (unsigned char)(qid >> 8); q[n++] = (unsigned char)(qid & 0xff);
    q[n++] = 0x01; q[n++] = 0x00;   // RD=1
    q[n++] = 0; q[n++] = 1;         // QDCOUNT
    q[n++] = 0; q[n++] = 0;         // ANCOUNT
    q[n++] = 0; q[n++] = 0;         // NSCOUNT
    q[n++] = 0; q[n++] = 0;         // ARCOUNT

    const char *p = host;
    while (*p) {
        const char *dot = strchr(p, '.');
        int len = dot ? (int)(dot - p) : (int)strlen(p);
        if (len <= 0 || len > 63 || n + len + 2 >= (int)sizeof(q)) return -1;
        q[n++] = (unsigned char)len;
        memcpy(q + n, p, (size_t)len); n += len;
        if (!dot) break;
        p = dot + 1;
    }
    q[n++] = 0;
    q[n++] = 0; q[n++] = 1;   // QTYPE = A
    q[n++] = 0; q[n++] = 1;   // QCLASS = IN

    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) return -1;
    struct timeval tv = {3, 0};
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));

    struct sockaddr_in ns;
    memset(&ns, 0, sizeof(ns));
    ns.sin_family = AF_INET;
    ns.sin_port = htons(53);
    if (inet_pton(AF_INET, dns_server(), &ns.sin_addr) != 1) { close(fd); return -1; }

    if (sendto(fd, q, (size_t)n, 0, (struct sockaddr *)&ns, sizeof(ns)) < 0) { close(fd); return -1; }
    unsigned char r[2048];
    int rn = (int)recv(fd, r, sizeof(r), 0);
    close(fd);
    if (rn < 12) return -1;

    int ancount = (r[6] << 8) | r[7];
    if (ancount < 1) return -1;

    int i = 12;
    while (i < rn && r[i]) {                    // 跳过问题段的 QNAME
        if ((r[i] & 0xC0) == 0xC0) { i += 2; break; }
        i += r[i] + 1;
    }
    if (i < rn && r[i] == 0) i++;
    i += 4;                                      // QTYPE + QCLASS

    for (int k = 0; k < ancount && i + 12 <= rn; k++) {
        if ((r[i] & 0xC0) == 0xC0) i += 2;       // 名字压缩指针
        else { while (i < rn && r[i]) i += r[i] + 1; i++; }
        if (i + 10 > rn) break;
        int type  = (r[i] << 8) | r[i + 1];
        int rdlen = (r[i + 8] << 8) | r[i + 9];
        i += 10;
        if (type == 1 && rdlen == 4) {
            if (i + 4 > rn) break;
            memcpy(out, r + i, 4);
            return 0;
        }
        i += rdlen;
    }
    return -1;
}

static int fill_addrinfo(const char *node, const char *service, const struct addrinfo *hints,
                         const struct in_addr *addr, struct addrinfo **res) {
    if (!res) return EAI_FAIL;
    struct addrinfo *ai = (struct addrinfo *)calloc(1, sizeof(*ai));
    struct sockaddr_in *sin = (struct sockaddr_in *)calloc(1, sizeof(*sin));
    if (!ai || !sin) { free(ai); free(sin); return EAI_MEMORY; }
    sin->sin_family = AF_INET;
    sin->sin_addr = *addr;
    long port = 0;
    if (service) {
        if (service[0] >= '0' && service[0] <= '9') port = atol(service);
        else if (!strcmp(service, "http")) port = 80;
        else if (!strcmp(service, "https")) port = 443;
        else if (!strcmp(service, "domain")) port = 53;
    }
    sin->sin_port = htons((unsigned short)port);
    ai->ai_family = AF_INET;
    ai->ai_socktype = hints ? hints->ai_socktype : SOCK_STREAM;
    ai->ai_protocol = hints ? hints->ai_protocol : (hints && hints->ai_socktype == SOCK_DGRAM ? IPPROTO_UDP : IPPROTO_TCP);
    ai->ai_addr = (struct sockaddr *)sin;
    ai->ai_addrlen = sizeof(*sin);
    ai->ai_next = NULL;
    *res = ai;
    (void)node;
    return 0;
}

// ── 唯一的解析入口：打印 + 三级解析 ──
static int hook_resolve(const char *node, const char *service, const struct addrinfo *hints,
                        struct addrinfo **res) {
    if (!node || !node[0]) return EAI_NONAME;

    struct in_addr a;
    if (inet_pton(AF_INET, node, &a) == 1) {              // ① 已经是点分 IP
        printf("[dns-hook] \"%s\" 已是 IP\n", node);
        return fill_addrinfo(node, service, hints, &a, res);
    }
    if (hosts_lookup(node, &a) == 0) {                     // ② /etc/hosts
        char ip[32]; inet_ntop(AF_INET, &a, ip, sizeof(ip));
        printf("[dns-hook] \"%s\" → %s （/etc/hosts）\n", node, ip);
        return fill_addrinfo(node, service, hints, &a, res);
    }
    if (dns_query_a(node, &a) == 0) {                      // ③ 自己发 DNS 查询
        char ip[32]; inet_ntop(AF_INET, &a, ip, sizeof(ip));
        printf("[dns-hook] \"%s\" → %s （UDP DNS）\n", node, ip);
        return fill_addrinfo(node, service, hints, &a, res);
    }
    printf("[dns-hook] \"%s\" ✗ 解析失败\n", node);
    return EAI_NODATA;
}

// ── 导出的替换符号（引擎走 bionic linker 会绑到这些）──
int getaddrinfo(const char *node, const char *service, const struct addrinfo *hints,
                struct addrinfo **res) {
    return hook_resolve(node, service, hints, res);
}

int android_getaddrinfo(const char *node, const char *service, const struct addrinfo *hints,
                        struct addrinfo **res) {
    return hook_resolve(node, service, hints, res);
}

struct hostent *gethostbyname(const char *name) {
    static struct hostent he;
    static struct in_addr addr;
    static char *aliases[1] = {NULL};
    static char *addr_list[2] = {NULL, NULL};
    struct in_addr a;
    if (inet_pton(AF_INET, name, &a) == 1 || hosts_lookup(name, &a) == 0 || dns_query_a(name, &a) == 0) {
        char ip[32];
        inet_ntop(AF_INET, &a, ip, sizeof(ip));
        printf("[dns-hook] gethostbyname(\"%s\") → %s\n", name, ip);
        addr = a;
        he.h_name = (char *)name;
        he.h_aliases = aliases;
        he.h_addrtype = AF_INET;
        he.h_length = 4;
        he.h_addr_list = addr_list;
        addr_list[0] = (char *)&addr;
        return &he;
    }
    printf("[dns-hook] gethostbyname(\"%s\") ✗\n", name);
    return NULL;
}

struct hostent *gethostbyname2(const char *name, int af) {
    if (af != AF_INET) return NULL;
    return gethostbyname(name);
}
