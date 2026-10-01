/* gbmprobe：独立复现 minigbm/mesa 的 GBM 分配，绕开符号拦截，直接拿真实错误。
 * 做法：dlopen("libgbm_mesa.so") → dlsym(gbm_create_device / gbm_bo_create / gbm_device_get_fd)
 *       打开 /dev/dri/renderD128 与 /dev/dri/card0，各试一次分配，打印返回值与 errno。
 * 用途：定位 SF 内 mapper 返回 NO_RESOURCES 的真实原因（在 virtio-gpu 上）。
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <dlfcn.h>
#include <sys/ioctl.h>

typedef void *(*fn_create)(int);
typedef void *(*fn_bo_create)(void *, unsigned, unsigned, unsigned, unsigned);
typedef int (*fn_get_fd)(void *);

int main(void) {
    printf("[gbmprobe] 开始\n");

    void *h = dlopen("libgbm_mesa.so", RTLD_NOW);
    printf("[gbmprobe] dlopen(libgbm_mesa.so) = %p  err=%s\n", h, h ? "-" : dlerror());
    if (!h) {
        h = dlopen("/system/lib64/libgbm_mesa.so", RTLD_NOW);
        printf("[gbmprobe] dlopen(/system/lib64/libgbm_mesa.so) = %p\n", h);
    }
    if (!h) { printf("[gbmprobe] 结束（无 gbm 库）\n"); return 1; }

    fn_create create = (fn_create)dlsym(h, "gbm_create_device");
    fn_bo_create bo_create = (fn_bo_create)dlsym(h, "gbm_bo_create");
    fn_get_fd get_fd = (fn_get_fd)dlsym(h, "gbm_device_get_fd");
    printf("[gbmprobe] dlsym: create=%p bo_create=%p get_fd=%p\n",
           (void *)create, (void *)bo_create, (void *)get_fd);
    if (!create || !bo_create) { printf("[gbmprobe] 结束（缺符号）\n"); return 1; }

    const char *nodes[] = { "/dev/dri/renderD128", "/dev/dri/card0" };
    for (int i = 0; i < 2; i++) {
        errno = 0;
        int fd = open(nodes[i], O_RDWR | O_CLOEXEC);
        printf("[gbmprobe] open(%s) = %d errno=%d(%s)\n", nodes[i], fd, errno, strerror(errno));
        if (fd < 0) continue;

        errno = 0;
        void *dev = create(fd);
        printf("[gbmprobe]   gbm_create_device(%d) = %p errno=%d(%s)\n", fd, dev, errno, strerror(errno));
        if (!dev) { close(fd); continue; }
        if (get_fd) printf("[gbmprobe]   gbm_device_get_fd = %d\n", get_fd(dev));

        /* SF 实测请求：1280x720 RGBA(fmt=1) usage=0x1b00 ⇒ GBM 侧等价 XRGB8888 */
        unsigned fmts[] = { 0x34325258 /* XRGB8888 */, 0x34325241 /* ARGB8888 */, 0x34324258 /* XBGR8888 */ };
        for (int k = 0; k < 3; k++) {
            errno = 0;
            void *bo = bo_create(dev, 1280, 720, fmts[k], 0x1b00);
            printf("[gbmprobe]   gbm_bo_create(1280x720 fmt=0x%x flags=0x1b00) = %p errno=%d(%s)\n",
                   fmts[k], bo, errno, strerror(errno));
            if (bo) {
                void *bo2 = bo_create(dev, 128, 128, fmts[k], 0x300);
                printf("[gbmprobe]   gbm_bo_create(128x128 fmt=0x%x flags=0x300) = %p errno=%d(%s)\n",
                       fmts[k], bo2, errno, strerror(errno));
                break;
            }
        }
        close(fd);
    }
    printf("[gbmprobe] 结束\n");
    return 0;
}
