/* dumb_gbm —— GBM 后端插件（替位 dri_gbm.so）：用 DRM dumb buffer 实现全部分配。
 *
 * 为什么需要（2026-10-02 P0）：waydroid wrapper（libgbm_mesa_wrapper）+ dri_gbm（mesa
 * gbm dri 后端）在 virtio-gpu + swrast 组合下，对 LINEAR|SCANOUT 用量的分配直接
 * SEGV（gbm_mesa_bo_create+0x21f 经 gbm_ops 表间接调用 → dri_gbm gbmint_get_backend），
 * allocator 进程死亡 ⇒ SF 全部 rc=5 ⇒ screencap 0 字节。mesa 公开的 backend ABI
 * （external/gbm_backend_abi.h，GBM_BACKEND_ABI_VERSION=0）允许外部插件提供
 * gbmint_get_backend —— 本文件即一个不依赖 gallium 的 dumb 后端：
 *   CREATE_DUMB（分配）/ MAP_DUMB+mmap（映射）/ PRIME_HANDLE_TO_FD（导出 dma-buf）。
 * virtio-gpu 内核驱动原生支持 dumb（fbcon 就建在它上面）；108 的 iris 走真驱动，
 * 不需要本插件（dri_gbm 只在 vendor/lib64 里被 wrapper 显式 dlopen）。
 *
 * ABI 契约（勿改函数名/布局，见 gbm_backend_abi.h）：
 *   const struct gbm_backend *gbmint_get_backend(const struct gbm_core *core);
 *   struct gbm_backend { struct gbm_backend_v0 { uint32_t backend_version;
 *                          const char *backend_name;
 *                          struct gbm_device *(*create_device)(int fd, uint32_t ver); } v0; }
 *   struct gbm_device { void *dummy; struct gbm_device_v0 v0; }
 *   struct gbm_bo     { struct gbm_device *gbm; struct gbm_bo_v0 v0; }
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <dlfcn.h>
#include <sys/ioctl.h>
#include <sys/mman.h>

/* ── drm ioctl（本地定义，避免引 libdrm 头）── */
#define DRM_IOCTL_BASE          'd'
#define DRM_IO(nr)              _IO(DRM_IOCTL_BASE, nr)
#define DRM_IOR(nr, type)       _IOR(DRM_IOCTL_BASE, nr, type)
#define DRM_IOW(nr, type)       _IOW(DRM_IOCTL_BASE, nr, type)
#define DRM_IOWR(nr, type)      _IOWR(DRM_IOCTL_BASE, nr, type)
#define DRM_CLOEXEC             0x80000

struct drm_mode_create_dumb {
    uint32_t height; uint16_t width; uint16_t bpp; uint32_t flags;
    uint32_t handle; uint32_t pitch; uint64_t size;
};
struct drm_mode_map_dumb { uint32_t handle; uint32_t pad; uint64_t offset; };
struct drm_mode_destroy_dumb { uint32_t handle; };
struct drm_gem_close { uint32_t handle; uint32_t pad; };
struct drm_prime_handle { uint32_t handle; uint32_t flags; int32_t fd; };

#define DRM_IOCTL_MODE_CREATE_DUMB   DRM_IOWR(0xB2, struct drm_mode_create_dumb)
#define DRM_IOCTL_MODE_MAP_DUMB     DRM_IOWR(0xB3, struct drm_mode_map_dumb)
#define DRM_IOCTL_MODE_DESTROY_DUMB DRM_IOWR(0xB4, struct drm_mode_destroy_dumb)
#define DRM_IOCTL_GEM_CLOSE          DRM_IOWR(0x09, struct drm_gem_close)
#define DRM_IOCTL_PRIME_HANDLE_TO_FD DRM_IOWR(0x2D, struct drm_prime_handle)
#define DRM_IOCTL_PRIME_FD_TO_HANDLE DRM_IOWR(0x2E, struct drm_prime_handle)

/* ── gbm 公开 ABI（与 libgbm_mesa 的 gbm.h 对齐的最小集）── */
#define GBM_BO_IMPORT_WL_BUFFER     0x5504
#define GBM_BO_IMPORT_EGL_IMAGE     0x5505
#define GBM_BO_IMPORT_FD            0x5506
#define GBM_BO_IMPORT_FD_MODIFIER   0x5507
#define GBM_BO_TRANSFER_READ        (1 << 0)
#define GBM_BO_TRANSFER_WRITE       (1 << 1)
#define GBM_BO_TRANSFER_READ_WRITE  (GBM_BO_TRANSFER_READ | GBM_BO_TRANSFER_WRITE)

union gbm_bo_handle { void *ptr; uint32_t u32; int32_t s32; uint64_t u64; };

struct gbm_bo_v0 {
    uint32_t width; uint32_t height; uint32_t stride; uint32_t format;
    union gbm_bo_handle handle;
    void *user_data; void (*destroy_user_data)(void *, void *);
};
struct gbm_bo { void *gbm; struct gbm_bo_v0 v0; };

struct gbm_device_v0 {
    const void *backend_desc;
    uint32_t backend_version; int fd; const char *name;
    void (*destroy)(void *);
    int (*is_format_supported)(void *, uint32_t, uint32_t);
    int (*get_format_modifier_plane_count)(void *, uint32_t, uint64_t);
    struct gbm_bo *(*bo_create)(void *, uint32_t, uint32_t, uint32_t, uint32_t,
                                const uint64_t *, const unsigned int);
    struct gbm_bo *(*bo_import)(void *, uint32_t, void *, uint32_t);
    void *(*bo_map)(struct gbm_bo *, uint32_t, uint32_t, uint32_t, uint32_t,
                    uint32_t, uint32_t *, void **);
    void (*bo_unmap)(struct gbm_bo *, void *);
    int (*bo_write)(struct gbm_bo *, const void *, size_t);
    int (*bo_get_fd)(struct gbm_bo *);
    int (*bo_get_planes)(struct gbm_bo *);
    union gbm_bo_handle (*bo_get_handle)(struct gbm_bo *, int);
    int (*bo_get_plane_fd)(struct gbm_bo *, int);
    uint32_t (*bo_get_stride)(struct gbm_bo *, int);
    uint32_t (*bo_get_offset)(struct gbm_bo *, int);
    uint64_t (*bo_get_modifier)(struct gbm_bo *);
    void (*bo_destroy)(struct gbm_bo *);
    void *(*surface_create)(void *, uint32_t, uint32_t, uint32_t, uint32_t,
                            const uint64_t *, const unsigned int);
    struct gbm_bo *(*surface_lock_front_buffer)(void *);
    void (*surface_release_buffer)(void *, struct gbm_bo *);
    int (*surface_has_free_buffers)(void *);
    void (*surface_destroy)(void *);
};
struct gbm_device { void *dummy; struct gbm_device_v0 v0; };

struct gbm_backend_v0 {
    uint32_t backend_version; const char *backend_name;
    struct gbm_device *(*create_device)(int fd, uint32_t backend_version);
};
struct gbm_backend { struct gbm_backend_v0 v0; };

struct gbm_import_fd_data { int fd; uint32_t width, height; uint32_t stride; uint32_t format; };
struct gbm_import_fd_modifier_data {
    uint32_t width, height, format; int num_fds;
    int fds[4]; int strides[4]; int offsets[4]; uint64_t modifier;
};

/* ── dumb 后端本体 ── */
static int g_debug = -1;
#define DBG(...) do { if (g_debug < 0) g_debug = getenv("GBM_DUMB_DEBUG") ? 1 : 0; \
                      if (g_debug) { fprintf(stderr, "[dumb-gbm] " __VA_ARGS__); fflush(stderr); } } while (0)

struct dumb_bo {
    struct gbm_device *gbm; struct gbm_bo_v0 v0;
    uint32_t gem_handle; uint64_t size;
};

/* gbm_device 首元素 detectable hack：指向 loader 的 gbm_create_device（同库同地址） */
static void *(*g_loader_create_device)(int);

static uint32_t fmt_bpp(uint32_t fmt) {
    switch (fmt) {
    case 0x34324241: /* ARGB8888 */
    case 0x34325258: /* XRGB8888 */
    case 0x34324258: /* ABGR8888 */
    case 0x34325842: /* XBGR8888 */
    case 0x34325241: /* BGRA8888 */
    case 0x34324142: /* RGBA8888(4CC 反序兜底) */
        return 32;
    case 0x36314752: /* RGB565 */
        return 16;
    default:
        return 32;   /* dumb 只认 bpp；未知格式按 32bpp 兜底 */
    }
}

static int dumb_is_format_supported(void *dev, uint32_t fmt, uint32_t usage) {
    (void)dev; (void)usage;
    switch (fmt) {
    case 0x34324241: case 0x34325258: case 0x34324258:
    case 0x34325842: case 0x34325241: case 0x34324142:
    case 0x36314752:
        return 1;
    default:
        return 0;
    }
}

static struct gbm_bo *dumb_bo_create(void *gbm, uint32_t w, uint32_t h, uint32_t fmt,
                                     uint32_t usage, const uint64_t *mods, unsigned nmods) {
    int fd = ((struct gbm_device *)gbm)->v0.fd;
    (void)usage; (void)mods; (void)nmods;   /* dumb 隐含 LINEAR；modifier 一律忽略 */
    struct drm_mode_create_dumb cd;
    memset(&cd, 0, sizeof(cd));
    cd.width = (uint16_t)w; cd.height = h; cd.bpp = (uint16_t)fmt_bpp(fmt); cd.flags = 0;
    if (ioctl(fd, DRM_IOCTL_MODE_CREATE_DUMB, &cd) != 0) {
        DBG("create %ux%u fmt=%08x 失败 errno=%d\n", w, h, fmt, errno);
        return NULL;
    }
    struct dumb_bo *bo = calloc(1, sizeof(*bo));
    if (!bo) return NULL;
    bo->gbm = gbm;
    bo->v0.width = w; bo->v0.height = h; bo->v0.stride = cd.pitch;
    bo->v0.format = fmt; bo->v0.handle.u32 = cd.handle;
    bo->gem_handle = cd.handle; bo->size = cd.size;
    DBG("create %ux%u fmt=%08x → handle=%u pitch=%u size=%llu\n",
        w, h, fmt, cd.handle, cd.pitch, (unsigned long long)cd.size);
    return (struct gbm_bo *)bo;
}

static struct gbm_bo *dumb_bo_import(void *gbm, uint32_t type, void *buf, uint32_t usage) {
    (void)usage;
    struct gbm_device *dev = gbm;
    int fd = -1;
    uint32_t w = 0, h = 0, stride = 0, fmt = 0;
    if (type == GBM_BO_IMPORT_FD) {
        struct gbm_import_fd_data *d = buf;
        fd = d->fd; w = d->width; h = d->height; stride = d->stride; fmt = d->format;
    } else if (type == GBM_BO_IMPORT_FD_MODIFIER) {
        struct gbm_import_fd_modifier_data *d = buf;
        if (d->num_fds < 1) return NULL;
        fd = d->fds[0]; w = d->width; h = d->height; stride = (uint32_t)d->strides[0]; fmt = d->format;
    } else {
        DBG("import type=0x%x 不支持（wl_buffer/egl_image 走不了 dumb）\n", type);
        return NULL;
    }
    struct drm_prime_handle ph;
    memset(&ph, 0, sizeof(ph));
    ph.fd = fd; ph.flags = DRM_CLOEXEC;
    if (ioctl(dev->v0.fd, DRM_IOCTL_PRIME_FD_TO_HANDLE, &ph) != 0) {
        DBG("import FD_TO_HANDLE 失败 errno=%d\n", errno);
        return NULL;
    }
    struct dumb_bo *bo = calloc(1, sizeof(*bo));
    if (!bo) return NULL;
    bo->gbm = gbm;
    bo->v0.width = w; bo->v0.height = h; bo->v0.stride = stride; bo->v0.format = fmt;
    bo->v0.handle.u32 = ph.handle;
    bo->gem_handle = ph.handle; bo->size = (uint64_t)stride * h;
    DBG("import %ux%u → handle=%u\n", w, h, ph.handle);
    return (struct gbm_bo *)bo;
}

static void *dumb_bo_map(struct gbm_bo *b, uint32_t x, uint32_t y, uint32_t w, uint32_t h,
                         uint32_t flags, uint32_t *stride, void **map_data) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    int fd = bo->gbm ? ((struct gbm_device *)bo->gbm)->v0.fd : -1;
    struct drm_mode_map_dumb md;
    memset(&md, 0, sizeof(md));
    md.handle = bo->gem_handle;
    if (ioctl(fd, DRM_IOCTL_MODE_MAP_DUMB, &md) != 0) {
        DBG("map MAP_DUMB 失败 errno=%d\n", errno);
        return NULL;
    }
    void *addr = mmap(NULL, bo->size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, md.offset);
    if (addr == MAP_FAILED) {
        DBG("map mmap 失败 errno=%d size=%llu\n", errno, (unsigned long long)bo->size);
        return NULL;
    }
    struct { struct dumb_bo *bo; void *addr; uint64_t size; } *md2 = malloc(sizeof(*md2));
    if (!md2) { munmap(addr, bo->size); return NULL; }
    md2->bo = bo; md2->addr = addr; md2->size = bo->size;
    *map_data = (void *)md2;
    if (stride) *stride = bo->v0.stride;
    uint32_t bpp = fmt_bpp(bo->v0.format) / 8;
    DBG("map ok addr=%p (+%u,%u %ux%u stride=%u)\n", addr, x, y, w, h, bo->v0.stride);
    return (char *)addr + (uint64_t)y * bo->v0.stride + (uint64_t)x * bpp;
    (void)flags;
}

static void dumb_bo_unmap(struct gbm_bo *b, void *map_data) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    if (!map_data) return;
    struct { struct dumb_bo *bo; void *addr; uint64_t size; } *md2 = map_data;
    munmap(md2->addr, md2->size);
    free(md2);
    (void)bo;
}

static int dumb_bo_write(struct gbm_bo *b, const void *buf, size_t n) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    uint32_t stride = 0; void *map_data = NULL;
    void *addr = dumb_bo_map(b, 0, 0, bo->v0.width, bo->v0.height,
                             GBM_BO_TRANSFER_WRITE, &stride, &map_data);
    if (!addr) return -1;
    size_t copy = n;
    if (copy > (size_t)bo->v0.stride * bo->v0.height) copy = (size_t)bo->v0.stride * bo->v0.height;
    memcpy(addr, buf, copy);
    dumb_bo_unmap(b, map_data);
    return 0;
}

static int dumb_bo_get_fd(struct gbm_bo *b) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    int fd = bo->gbm ? ((struct gbm_device *)bo->gbm)->v0.fd : -1;
    struct drm_prime_handle ph;
    memset(&ph, 0, sizeof(ph));
    ph.handle = bo->gem_handle; ph.flags = DRM_CLOEXEC;
    if (ioctl(fd, DRM_IOCTL_PRIME_HANDLE_TO_FD, &ph) != 0) {
        DBG("get_fd PRIME_HANDLE_TO_FD 失败 errno=%d（handle=%u）\n", errno, bo->gem_handle);
        return -1;
    }
    DBG("get_fd → %d\n", ph.fd);
    return ph.fd;
}

static union gbm_bo_handle dumb_bo_get_handle(struct gbm_bo *b, int plane) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    union gbm_bo_handle h;
    memset(&h, 0, sizeof(h));
    if (plane == 0) h.u32 = bo->gem_handle;
    return h;
}

static void dumb_bo_destroy(struct gbm_bo *b) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    int fd = bo->gbm ? ((struct gbm_device *)bo->gbm)->v0.fd : -1;
    struct drm_gem_close gc;
    memset(&gc, 0, sizeof(gc));
    gc.handle = bo->gem_handle;
    ioctl(fd, DRM_IOCTL_GEM_CLOSE, &gc);
    DBG("destroy handle=%u\n", bo->gem_handle);
    free(bo);
}

static void dumb_device_destroy(void *gbm) { free(gbm); }

/* modifier 查询：LINEAR 支持 1 平面；其余 modifier 不支持（调用方回落普通创建） */
static int dumb_get_format_modifier_plane_count(void *dev, uint32_t fmt, uint64_t mod) {
    (void)dev; (void)fmt;
    return mod == 0 ? 1 : 0;
}
static int dumb_bo_get_planes(struct gbm_bo *b) { (void)b; return 1; }
static int dumb_bo_get_plane_fd(struct gbm_bo *b, int plane) {
    return plane == 0 ? dumb_bo_get_fd(b) : -1;
}
static uint32_t dumb_bo_get_stride(struct gbm_bo *b, int plane) {
    struct dumb_bo *bo = (struct dumb_bo *)b;
    return plane == 0 ? bo->v0.stride : 0;
}
static uint32_t dumb_bo_get_offset(struct gbm_bo *b, int plane) {
    (void)b; (void)plane; return 0;
}
static uint64_t dumb_bo_get_modifier(struct gbm_bo *b) {
    (void)b; return 0;   /* DRM_FORMAT_MOD_LINEAR */
}

static struct gbm_device *dumb_create_device(int fd, uint32_t backend_version) {
    struct gbm_device *dev = calloc(1, sizeof(*dev));
    if (!dev) return NULL;
    dev->dummy = g_loader_create_device;      /* loader 同地址符号（detectable hack） */
    dev->v0.backend_version = backend_version;
    dev->v0.fd = fd;
    dev->v0.name = "dumb";
    dev->v0.destroy = dumb_device_destroy;
    dev->v0.is_format_supported = dumb_is_format_supported;
    dev->v0.get_format_modifier_plane_count = dumb_get_format_modifier_plane_count;
    dev->v0.bo_create = dumb_bo_create;
    dev->v0.bo_import = dumb_bo_import;
    dev->v0.bo_map = dumb_bo_map;
    dev->v0.bo_unmap = dumb_bo_unmap;
    dev->v0.bo_write = dumb_bo_write;
    dev->v0.bo_get_fd = dumb_bo_get_fd;
    dev->v0.bo_get_planes = dumb_bo_get_planes;
    dev->v0.bo_get_handle = dumb_bo_get_handle;
    dev->v0.bo_get_plane_fd = dumb_bo_get_plane_fd;
    dev->v0.bo_get_stride = dumb_bo_get_stride;
    dev->v0.bo_get_offset = dumb_bo_get_offset;
    dev->v0.bo_get_modifier = dumb_bo_get_modifier;
    dev->v0.bo_destroy = dumb_bo_destroy;
    DBG("create_device fd=%d version=%u\n", fd, backend_version);
    return dev;
}

static const struct gbm_backend backend = {
    .v0 = {
        .backend_version = 0,        /* GBM_BACKEND_ABI_VERSION 0 */
        .backend_name = "dumb",
        .create_device = dumb_create_device,
    },
};

const struct gbm_backend *gbmint_get_backend(const struct gbm_core *core) {
    (void)core;
    /* loader 的 gbm_create_device：用于 gbm_device.detectable hack。
     * 插件与 loader 同进程，RTLD_DEFAULT 必中（libgbm_mesa 导出它）。 */
    if (!g_loader_create_device) {
        void *h = dlopen("libgbm_mesa.so", RTLD_NOW | RTLD_NOLOAD);
        if (!h) h = dlopen("libgbm_mesa.so", RTLD_NOW);
        if (h) g_loader_create_device = dlsym(h, "gbm_create_device");
    }
    DBG("get_backend → v0（dumb）\n");
    return &backend;
}
