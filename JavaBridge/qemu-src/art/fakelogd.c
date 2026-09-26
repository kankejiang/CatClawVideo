/* fake logd — 只为把 guest 里被丢掉的 Android 日志捞回串口。
 *
 * 为什么要它（2026-09-25 实测）：app_process64 装上属性垫片后不再报 ABI 错，但**静默 rc=0**；
 * 查 Android 源码知道 `AndroidRuntime::start()` 里 `startVm` 失败是 `return`（不是 abort），
 * 失败原因走 liblog → `/dev/socket/logdw`；guest 里没有 logd，bionic 的用户态实现就把消息**整条丢掉**。
 * 于是这里 bind 那个 socket，把收到的数据报里的可打印串倒出来。
 *
 * 协议（Android 8/9 的 writer）：一个数据报 = [uint32 log_id][description\0][android_log_event_t{len,type,msg\0}]
 * —— 不解析结构，直接抽字符串，够用且不会因版本差异失效。
 *
 * 注意：**不要**同时造 `/dev/socket/logd`（那是读端）。libbase 用它的存在与否决定走 logd 还是 stderr；
 * 我们只想要 logdw 这条路，其余留在 stderr 上更直观。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/stat.h>

#define PATH_LOGD_WR "/dev/socket/logdw"

static void dump(unsigned char *b, ssize_t n) {
    char run[512];
    size_t k = 0;
    for (ssize_t i = 0; i < n; i++) {
        unsigned char c = b[i];
        if (c >= 0x20 && c < 0x7f) {
            if (k + 1 < sizeof run) run[k++] = (char) c;
        } else {
            if (k >= 4) { run[k] = 0; printf("[logd] %s\n", run); }
            k = 0;
        }
    }
    if (k >= 4) { run[k] = 0; printf("[logd] %s\n", run); }
    fflush(stdout);
}

int main(void) {
    mkdir("/dev/socket", 0777);
    unlink(PATH_LOGD_WR);
    int fd = socket(AF_UNIX, SOCK_DGRAM, 0);
    if (fd < 0) { perror("socket"); return 1; }
    struct sockaddr_un sa;
    memset(&sa, 0, sizeof sa);
    sa.sun_family = AF_UNIX;
    snprintf(sa.sun_path, sizeof sa.sun_path, "%s", PATH_LOGD_WR);
    if (bind(fd, (struct sockaddr *) &sa, sizeof(sa_family_t) + strlen(sa.sun_path) + 1) < 0) {
        perror("bind " PATH_LOGD_WR);
        return 1;
    }
    printf("[fakelogd] listening on %s\n", PATH_LOGD_WR);
    fflush(stdout);
    unsigned char buf[65536];
    ssize_t n;
    while ((n = recv(fd, buf, sizeof buf, 0)) > 0) dump(buf, n);
    return 0;
}
