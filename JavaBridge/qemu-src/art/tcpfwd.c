/* tcpfwd —— 极简并发 TCP 端口转发器（x86 ART guest 专用，2026-10-03）
 *
 * 背景：GoProxy 的 pvideo 只绑 127.0.0.1:5266，slirp hostfwd 从 eth0 进来直接被
 * RST（2026-09-26 实测教训）；busybox nc -l 单发一次只能服务一个连接，播放器的
 * 视频/弹幕/探针并发连接会卡死在 backlog。内核又没有 netfilter（无 iptables 可用）。
 *
 * 用法: tcpfwd <listen_port> <dst_ip> <dst_port>
 * 模型: accept + fork per connection（父进程立即回收 SIGCHLD），每连接 select 双向泵。
 * 静态编译（NDK x86_64，-static），无任何运行时依赖，随 initrd 走。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <signal.h>
#include <errno.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>

static void pump(int a, int b) {
    static char buf[65536];
    ssize_t n;
    fd_set rs;
    for (;;) {
        FD_ZERO(&rs);
        FD_SET(a, &rs);
        FD_SET(b, &rs);
        if (select((a > b ? a : b) + 1, &rs, NULL, NULL, NULL) <= 0) {
            if (errno == EINTR) continue;
            return;
        }
        if (FD_ISSET(a, &rs)) {
            n = read(a, buf, sizeof buf);
            if (n <= 0) return;
            if (write(b, buf, (size_t)n) != n) return;
        }
        if (FD_ISSET(b, &rs)) {
            n = read(b, buf, sizeof buf);
            if (n <= 0) return;
            if (write(a, buf, (size_t)n) != n) return;
        }
    }
}

int main(int argc, char **argv) {
    if (argc != 4) {
        fprintf(stderr, "usage: %s <listen_port> <dst_ip> <dst_port>\n", argv[0]);
        return 2;
    }
    int lp = atoi(argv[1]), dp = atoi(argv[3]);
    signal(SIGCHLD, SIG_IGN);          /* 子进程退出即内核回收，不留僵尸 */
    signal(SIGPIPE, SIG_IGN);

    int s = socket(AF_INET, SOCK_STREAM, 0);
    if (s < 0) { perror("socket"); return 1; }
    int one = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);

    struct sockaddr_in la;
    memset(&la, 0, sizeof la);
    la.sin_family = AF_INET;
    la.sin_addr.s_addr = htonl(INADDR_ANY);   /* 绑 0.0.0.0：slirp 从 eth0 进得来 */
    la.sin_port = htons((unsigned short)lp);
    if (bind(s, (struct sockaddr *)&la, sizeof la) < 0) { perror("bind"); return 1; }
    if (listen(s, 32) < 0) { perror("listen"); return 1; }

    struct sockaddr_in da;
    memset(&da, 0, sizeof da);
    da.sin_family = AF_INET;
    da.sin_port = htons((unsigned short)dp);
    if (inet_pton(AF_INET, argv[2], &da.sin_addr) != 1) {
        fprintf(stderr, "bad dst ip: %s\n", argv[2]);
        return 2;
    }

    fprintf(stderr, "tcpfwd: *:%d -> %s:%d\n", lp, argv[2], dp);
    for (;;) {
        int c = accept(s, NULL, NULL);
        if (c < 0) {
            if (errno == EINTR) continue;
            continue;
        }
        pid_t pid = fork();
        if (pid == 0) {
            close(s);
            int u = socket(AF_INET, SOCK_STREAM, 0);
            if (u < 0) _exit(1);
            if (connect(u, (struct sockaddr *)&da, sizeof da) < 0) _exit(1);
            pump(c, u);
            _exit(0);
        }
        close(c);   /* 父进程不碰连接 */
    }
}
