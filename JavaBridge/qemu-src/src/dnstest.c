// bionic 网络自检：验证在「无 netd 的 initramfs」里 getaddrinfo + connect 是否可用。
// 迅雷引擎是 bionic 程序，它的 DNS 走 Android 的 resolver；若这里失败，
// 引擎就永远不会建立连接（表现为任务状态 RUNNING 但 /proc/net/tcp 全空）。
#include <stdio.h>
#include <string.h>
#include <errno.h>
#include <netdb.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <stdlib.h>
#include <fcntl.h>

static void try_host(const char *host, const char *port) {
    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    printf("[dns] getaddrinfo(\"%s\") ... ", host);
    int rc = getaddrinfo(host, port, &hints, &res);
    if (rc != 0) {
        printf("失败: %s (rc=%d)\n", gai_strerror(rc), rc);
        return;
    }
    char ip[64] = "?";
    if (res->ai_family == AF_INET)
        inet_ntop(AF_INET, &((struct sockaddr_in *)res->ai_addr)->sin_addr, ip, sizeof(ip));
    else if (res->ai_family == AF_INET6)
        inet_ntop(AF_INET6, &((struct sockaddr_in6 *)res->ai_addr)->sin6_addr, ip, sizeof(ip));
    printf("成功 → %s\n", ip);

    // 顺带测一次 TCP connect（证明出站通路）
    int fd = socket(res->ai_family, SOCK_STREAM, 0);
    if (fd < 0) { printf("[dns]   socket 失败 errno=%d\n", errno); freeaddrinfo(res); return; }
    struct timeval tv = {5, 0};
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    fcntl(fd, F_SETFL, fcntl(fd, F_GETFL, 0));
    printf("[dns]   connect(%s:%s) ... ", ip, port);
    if (connect(fd, res->ai_addr, res->ai_addrlen) == 0) printf("成功 ✅\n");
    else printf("失败 errno=%d (%s)\n", errno, strerror(errno));
    close(fd);
    freeaddrinfo(res);
}

int main(void) {
    setvbuf(stdout, NULL, _IONBF, 0);
    printf("===== bionic 网络自检 =====\n");

    // 1) 纯 IP 的 TCP（不依赖 DNS）
    struct sockaddr_in sa;
    memset(&sa, 0, sizeof(sa));
    sa.sin_family = AF_INET;
    sa.sin_port = htons(53);
    inet_pton(AF_INET, "10.0.2.3", &sa.sin_addr);
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    printf("[dns] 裸 IP TCP connect(10.0.2.3:53) ... ");
    if (fd >= 0 && connect(fd, (struct sockaddr *)&sa, sizeof(sa)) == 0) printf("成功 ✅\n");
    else printf("失败 errno=%d (%s)\n", errno, strerror(errno));
    if (fd >= 0) close(fd);

    // 2) DNS 解析（国内常见域名）
    try_host("www.baidu.com", "80");
    try_host("www.qq.com", "80");

    printf("[dns] /etc/resolv.conf 存在? ");
    FILE *f = fopen("/etc/resolv.conf", "r");
    if (f) { char b[256]; size_t n = fread(b, 1, sizeof(b) - 1, f); b[n] = 0;
             printf("是：%s", b); fclose(f); }
    else printf("否 (errno=%d)\n", errno);
    printf("===== 自检结束 =====\n");
    return 0;
}
