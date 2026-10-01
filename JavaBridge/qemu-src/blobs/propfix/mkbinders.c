// mkbinders.c —— B1.1/P0.1：用 binderfs 的 BINDER_CTL_ADD ioctl 建**独立**的 binder 设备节点
//
// 为什么必须这么做：Android 的 HIDL 注册走 /dev/hwbinder，它必须是**独立**的 binder 实例。
// 我们先前只能把 /dev/hwbinder 符号链接到 /dev/binder（同一实例），结果 services 注册时报
//   Could not get transport for ...::IAllocator/default: Status(EX_TRANSACTION_FAILED): 'BAD_TYPE: '
//   Failed to register graphics IAllocator 4.0 service.
// binderfs 挂载后不会自动生成节点（只有 binder-control），必须由进程对 binder-control 发 ioctl 创建。
//
// 编（108 上 NDK）：
//   x86_64-linux-android33-clang -O2 -o mkbinders mkbinders.c
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/mount.h>
#include <sys/stat.h>
#include <unistd.h>

// linux/android/binderfs.h 里：
//   struct binderfs_device { char name[256]; __u32 major; __u32 minor; };
//   #define BINDER_CTL_ADD  _IOWR('b', 1, struct binderfs_device)
#define BINDERFS_MAX_NAME 255
struct binderfs_device {
    char name[BINDERFS_MAX_NAME + 1];
    unsigned int major;
    unsigned int minor;
};
#define BINDER_CTL_ADD _IOWR('b', 1, struct binderfs_device)

int main(void) {
    const char *dir = "/dev/binderfs";
    mkdir(dir, 0755);
    if (!mount("binder", dir, "binder", 0, NULL)) {
        fprintf(stderr, "[mkbinders] binderfs 已挂载到 %s\n", dir);
    } else {
        fprintf(stderr, "[mkbinders] mount binderfs: %s（若已挂载可忽略）\n", strerror(errno));
    }

    char ctl[128];
    snprintf(ctl, sizeof(ctl), "%s/binder-control", dir);
    int fd = open(ctl, O_RDWR | O_CLOEXEC);
    if (fd < 0) {
        fprintf(stderr, "[mkbinders] 打不开 %s: %s\n", ctl, strerror(errno));
        return 1;
    }

    const char *names[] = { "binder", "hwbinder", "vndbinder" };
    for (unsigned i = 0; i < sizeof(names) / sizeof(names[0]); i++) {
        struct binderfs_device dev;
        memset(&dev, 0, sizeof(dev));
        snprintf(dev.name, sizeof(dev.name), "%s", names[i]);
        if (ioctl(fd, BINDER_CTL_ADD, &dev) == 0) {
            fprintf(stderr, "[mkbinders] 建好 %s (major=%u minor=%u)\n", dev.name, dev.major, dev.minor);
            // 让 /dev/<name> 指向 binderfs 里的真节点（独立实例）
            char link[128], target[160];
            snprintf(link, sizeof(link), "/dev/%s", names[i]);
            snprintf(target, sizeof(target), "%s/%s", dir, names[i]);
            unlink(link);
            if (symlink(target, link) == 0)
                fprintf(stderr, "[mkbinders]   /dev/%s -> %s\n", names[i], target);
            else
                fprintf(stderr, "[mkbinders]   链接 /dev/%s 失败: %s\n", names[i], strerror(errno));
        } else {
            fprintf(stderr, "[mkbinders] ioctl 建 %s 失败: %s（已存在则正常）\n", names[i], strerror(errno));
        }
    }
    close(fd);
    return 0;
}
