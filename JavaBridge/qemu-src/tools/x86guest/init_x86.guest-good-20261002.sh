#!/bin/busybox sh
# ★ 2026-10-02 从**运行中的 guest 原样导出**的 /init（已修好一处致命 bug）。
#   导出：adb -s 127.0.0.1:18603 exec-out "cat /init" > init.sh
#   修掉的问题：曾有一行被写成（同一行里赋值表达式重复了一次）
#       PORT=$(getarg guardport)PORT=$(getarg guardport); [ -n "$PORT" ] || PORT=18600
#   shell 解析成单个赋值 PORT=<getarg guardport 的输出>PORT=<再来一次>，
#   于是 PORT 变成 "18600PORT=18600"，喂给 artlaunch 后
#   GuestMain.java:24 的 Integer.parseInt 抛 NumberFormatException → 桥进程秒退（rc=1）
#   → guest 端口 18600 永不监听 → 订阅里 47 个 jar 爬虫站全不可用
#   → 首页加载不出来（用户原话「改了也是白改」）。
# ⚠ 重建 initrd 时若又从 108 的基础 cpio 取 init，请先核对这一行再打包。
# ⚠ 导出时不要用 adb shell（会把 proppreload 的启动日志混进首行，shebang 失效 → ENOEXEC）。
# x86 mini guest 的 /init —— QEMU(WHPX/KVM) 里跑「Android 13 ART + 桥」。
# 蓝本：aarch64 版 init.tmpl.sh；差异：APEX 布局、CATCLAW_BCP 环境变量、dalvik-cache/x86_64。
BB=/bin/busybox
export PATH=/system/bin:/bin
getarg() { $BB sed -n 's/.*'"$1"'=\([^ ]*\).*/\1/p' /proc/cmdline 2>/dev/null | $BB head -1; }

$BB mkdir -p /proc /sys /dev /tmp /data/catclaw
$BB mount -t proc proc /proc 2>/dev/null
$BB mount -t sysfs sys /sys 2>/dev/null
$BB mount -t devtmpfs devtmpfs /dev 2>/dev/null || $BB mount -t tmpfs tmpfs /dev -o mode=0755 2>/dev/null
# insmod 顺序即依赖顺序：ring → virtio 核心 → modern_dev/legacy → pci → blk → failover 系 → net

# B1.1: binder 三实例（binderfs 不可用时的正解）
[ -f /modules/binder_linux.ko ] && $BB insmod /modules/binder_linux.ko devices=binder,hwbinder,vndbinder && echo '[init] binder: 三实例已建'
for m in crc16 crc32c_generic mbcache jbd2 ext4 binfmt_misc; do
    [ -f /modules/$m.ko ] || continue
    echo "[init] insmod $m: $($BB insmod /modules/$m.ko 2>&1)" || true
done
for m in virtio_ring virtio virtio_pci_modern_dev virtio_pci_legacy_dev virtio_pci virtio_blk failover net_failover virtio_net binder_linux; do
    echo "[init] insmod $m: $($BB insmod /modules/$m.ko 2>&1)" || true
done

# ── 持久化数据盘（datadev=）：/data 落宿主本地 ext4——模拟器 userdata 同款（2026-10-02）──
# 首启 mke2fs 建文件系统；之后每次挂载（ext4 日志自动重放，QEMU 硬杀安全）。
# 盘上即最新状态：宿主的偏好回灌在 datadev 存在时由桥侧跳过（JavaSpiderRuntime）。
PDD=$(getarg datadev)
if [ -n "$PDD" ] && [ -b "$PDD" ]; then
    echo "[persist] 盘节点: $PDD; 内核 ext4 支持: $(grep -c ext4 /proc/filesystems 2>/dev/null)（0=无!）; partitions:"; grep vdc /proc/partitions 2>/dev/null
    export LD_LIBRARY_PATH=/system/lib64
    export MKE2FS_CONFIG=/system/etc/mke2fs.conf
    # ★ 预挂载强制 fsck（2026-10-03）：QEMU 硬杀可能留下脏 dentry（EUCLEAN，
    #   "Structure needs cleaning"，rm/stat 都救不了），只有离线 e2fsck 能自愈。
    #   幂等：干净盘上它秒过。不跑这步，jar 的"删旧→下载新"更新流程会卡死在脏目录上。
    #   ⚠ 用 PATH 上的独立 e2fsck（e2fsprogs），busybox 无此 applet（"applet not found"）。
    e2fsck -y "$PDD" > /tmp/fsck.log 2>&1
    echo "[persist] 预挂载 fsck rc=$? 尾行: $($BB tail -1 /tmp/fsck.log 2>/dev/null)"
    $BB mount -t ext4 "$PDD" /data
    echo "[persist] mount RC=$?"
    if ! $BB mount | $BB grep -q "on /data "; then
        e2fsck -y "$PDD" >/dev/null 2>&1
        $BB mount -t ext4 "$PDD" /data
        echo "[persist] fsck 后 mount RC=$?"
    fi
    if ! $BB mount | $BB grep -q "on /data "; then
        echo "[persist] mke2fs 全量输出:"
        mke2fs -F "$PDD" 2>&1 | $BB tail -4
        $BB mount -t ext4 "$PDD" /data
        echo "[persist] mke2fs 后 mount RC=$?"
    fi
    if $BB mount | $BB grep -q "on /data "; then
        echo "[persist] /data -> 持久盘 $PDD（跨重启存活）"
        # ★ 无损扩容（2026-10-03）：宿主把镜像文件 SetLength 调大后，ext4 自己的
        #   超级块还停在旧容量上（df 仍显示旧值），必须在挂载状态下 resize2fs 扩满。
        #   幂等：已经扩满时它什么也不做。不跑这一步，改容量等于白改。
        e2fsck -f -p "$PDD" >/dev/null 2>&1
        echo "[persist] resize2fs: $(resize2fs "$PDD" 2>&1 | $BB tail -2 | $BB tr '
' ' ')"
                $BB mkdir -p /data/catclaw /data/local/tmp /data/dalvik-cache/x86_64 /data/media /data/files/fishdanmu /data/cache/pvc /data/files/moyu_go
    else
        echo "[persist] 警告：/data 持久盘未挂成（退回内存态）"
    fi
fi

PDD_UNUSED_MARKER=$(getarg datadev)
if [ -n "$PDD" ] && [ -b "$PDD" ]; then
    export LD_LIBRARY_PATH=/system/lib64
    export MKE2FS_CONFIG=/system/etc/mke2fs.conf
    if ! $BB mount -t ext4 "$PDD" /data 2>/dev/null; then
        echo "[persist] 直接挂载失败（首启或脏日志），e2fsck -p 输出:"
        e2fsck -p "$PDD" 2>&1 | $BB head -5
        if ! $BB mount -t ext4 "$PDD" /data 2>/dev/null; then
            echo "[persist] 仍失败 → mke2fs 建文件系统（首启路径）:"
            mke2fs -q -F "$PDD" 2>&1 | $BB head -5
            $BB mount -t ext4 "$PDD" /data 2>&1 | $BB head -3
        fi
    fi
    if $BB mount 2>/dev/null | $BB grep -q "on /data "; then
        echo "[persist] /data -> 持久盘 $PDD（跨重启存活）"
        $BB mkdir -p /data/catclaw /data/local/tmp /data/dalvik-cache/x86_64 /data/media /data/files/fishdanmu /data/cache/pvc /data/files/moyu_go
    else
        echo "[persist] 警告：/data 持久盘未挂成（退回内存态）"
    fi
fi

PORT=$(getarg guardport); [ -n "$PORT" ] || PORT=18600
DP=$(getarg ctrl)
if [ -n "$DP" ]; then export CATCLAW_DNS=10.0.2.2:$DP; echo "[dns] 走宿主转发 $CATCLAW_DNS"; fi
echo "=== CatClaw x86 ART guest begin (bridgeport=$PORT) ==="

$BB ifconfig lo 127.0.0.1 netmask 255.0.0.0 up 2>&1
$BB ifconfig eth0 10.0.2.15 netmask 255.255.255.0 up 2>&1
$BB route add default gw 10.0.2.2 2>&1
$BB mkdir -p /etc
echo "nameserver 10.0.2.3" > /etc/resolv.conf
echo "search lan" >> /etc/resolv.conf

/fakelogd &
$BB sleep 1

export ANDROID_ROOT=/system ANDROID_DATA=/data ANDROID_STORAGE=/storage
export ANDROID_ART_ROOT=/apex/com.android.art
export ANDROID_I18N_ROOT=/apex/com.android.i18n
export ANDROID_TZDATA_ROOT=/apex/com.android.tzdata
export TMPDIR=/data/local/tmp
export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64
# 13 的 boot classpath：core 五件在 ART apex，framework 件在 /system/framework
# BCP 全部走 /system 路径（core jar 已从 apex 拷出）——避免触发 apex linker namespace
export CATCLAW_JVM_EXTRA="-Xnorelocate -Xcheck:jni -Ximage:/system/framework/x86_64/boot.art:/system/framework/x86_64/boot-core-libart.art:/system/framework/x86_64/boot-core-icu4j.art:/system/framework/x86_64/boot-okhttp.art:/system/framework/x86_64/boot-bouncycastle.art:/system/framework/x86_64/boot-apache-xml.art:/system/framework/x86_64/boot-conscrypt.art:/system/framework/x86_64/boot-framework.art:/system/framework/x86_64/boot-ext.art:/system/framework/x86_64/boot-telephony-common.art:/system/framework/x86_64/boot-voip-common.art:/system/framework/x86_64/boot-ims-common.art:/system/framework/x86_64/boot-android.hidl.base-V1.0-java.art:/system/framework/x86_64/boot-android.hidl.manager-V1.0-java.art:/system/framework/x86_64/boot-android.test.base.art"
#（-Ximage 整体覆盖 ART13 默认镜像 spec —— 那个 spec 是 AOSP boot.art+
#  boot-framework.art+双 prof 布局，与我们的 multi-image 组件集对不上；
#  -Xbootclasspath-locations 不重定向镜像，2026-09-29 三轮实测唯一可行）
export CATCLAW_BCP="/system/javalib/core-oj.jar:/system/javalib/core-libart.jar:/system/javalib/core-icu4j.jar:/system/javalib/okhttp.jar:/system/javalib/bouncycastle.jar:/system/javalib/apache-xml.jar:/system/javalib/conscrypt.jar:/system/framework/framework.jar:/system/framework/ext.jar:/system/framework/telephony-common.jar:/system/framework/voip-common.jar:/system/framework/ims-common.jar:/system/framework/android.hidl.base-V1.0-java.jar:/system/framework/android.hidl.manager-V1.0-java.jar:/system/framework/android.test.base.jar"

# ── ARM 转译内核层注册（binfmt_misc）──
# ndk_translation 的 arm64 runner 走 execve 注册：arm64 ELF（e_machine 低字节 b7=183）由
# /system/bin/ndk_translation_program_runner_binfmt_misc_arm64 接管。
# PreInitializeNativeBridge 会 exec arm64 wrapper，不注册则 ENOEXEC/x86 linker 报架构错。
mkdir -p /binfmt_misc
mount -t binfmt_misc none /binfmt_misc 2>/dev/null
# 2026-10-03 定因：busybox echo 不解释 \x 转义 → 注册的是字面文本 magic 永不命中；
# 且 binfmt_misc 注册按 C 字符串解析，magic 内嵌 \000 会被截断 → 截断前缀命中所有
# 64 位 ELF → 解释器递归 → 全机 exec ELOOP。方案：单字节 magic offset 18
# （e_machine=0xB7=AArch64），无 NUL 毒免疫、ET_EXEC/ET_DYN 通吃；解释器用
# qemu-user（ndk runner 跑 Go 二进制 1s 内静默 139）。flags 字段不能省（尾冒号）。
printf ':arm64:M:18:\267::/bin/qemu-aarch64-static:' > /binfmt_misc/register 2>/dev/null && echo "[init] binfmt arm64 已注册" || echo "[init] binfmt arm64 注册失败"
export CATCLAW_SIGLOG=1
printf '#!/bin/busybox sh\necho "NC-ARGS: $@" > /dev/ttyS0\nexec /bin/busybox nc "$@"\n' > /bin/nc && chmod +x /bin/nc


# ── 迅雷段（合并磁力）：cmdline thunderport= 存在时拉起 ARM harness ──
# ARM harness + 迅雷 SDK（只有 ARM 版）在 x86 guest 里经 qemu-aarch64-static
# 用户态转译跑（108 已实测：引擎初始化/BT 边下边播全链路通）。ndk_translation
# 路线（expA）harness 秒退不可用。thunderport 缺省时本段整体休眠（纯桥 VM 零开销）。
TP=$(getarg thunderport)
if [ -n "$TP" ] && [ -x /harness ]; then
    export CTRL_HOST="10.0.2.2"          # SLIRP 宿主侧（控制端 QemuGuestEngine）
    export CTRL_PORT="$TP"
    export PROXY_PORT="20080"            # guest 媒体代理口（宿主 hostfwd -:20080 对准它）
    export P2SP_SECS="0" DL_SECS="0"
    export BLK_DEV="$(getarg blkdev)"    # 数据面块设备；缺位时 harness 回退纯转发
    export QCO="${QCO:-1}"
    # ARM bionic 引擎库优先（x86 的 /system/lib64 是错误架构，bionic 会跳过继续找）
    # ARM 引擎库路径**只能**挂在 harness 命令上（见下方 exec 行），绝不能 export 进 init 环境：
    # 本段跑在 artlaunch 之前，一旦进环境，x86 的 artlaunch 就会去 /thunder-arm/system/lib64
    # 找 libdl.so → CANNOT LINK EXECUTABLE ... is for EM_AARCH64 (183) instead of EM_X86_64 (62)
    # → 桥退出码 1 →「点一次磁力 = 打死整个爬虫桥」（2026-09-30 实测）。
    SD=$(getarg swapdev)
    if [ -n "$SD" ] && [ -b "$SD" ]; then
        $BB mkswap "$SD" 2>/dev/null
        $BB swapon "$SD" 2>/dev/null && echo "[thunder] swap on $SD"
    fi
    i=0
    while [ $i -lt 30 ] && ! $BB ifconfig eth0 2>/dev/null | $BB grep -q 10.0.2.15; do
        $BB sleep 1; i=$((i+1))
    done
    $BB mkdir -p /thunder-data 2>/dev/null
    # 监督循环：harness 退出（崩溃/宿主 EXIT 重置）→ 2s 后拉起；引擎任务表随进程清空，
    # /thunder-data 是 VM 级 tmpfs、块设备数据在宿主镜像 —— 都不随进程死。
    (
      while true; do
        LD_LIBRARY_PATH=/data/catclaw/art/lib:/thunder-arm/system/lib64 /qemu-aarch64-static -L /thunder-arm /harness >>/thunder.log 2>&1
        echo "[thunder] harness 退出（code=$?），2s 后重启（引擎任务表清空）"
        $BB sleep 2
      done
    ) &
    echo "[thunder] harness 监督循环已起（CTRL_PORT=$TP BLK_DEV=${BLK_DEV:-无}，日志 /thunder.log）"
fi

export LD_LIBRARY_PATH=/apex/com.android.art/lib64:/apex/com.android.os.statsd/lib64:/system/lib64

# ── B1.1：virtio-gpu / DRM 模块（→ /dev/dri）──
if [ -f /b1/mods.tar.gz ]; then
    $BB tar xzf /b1/mods.tar.gz -C /modules/ && echo "[mods] 解包完成"
    for m in drm drm_kms_helper drm_shmem_helper cec drm_display_helper virtio_dma_buf virtio-gpu drm_ttm_helper ttm; do
        [ -f /modules/$m.ko ] || continue
        $BB insmod /modules/$m.ko >/dev/null 2>&1 && echo "[mods] insmod $m 成功" || echo "[mods] insmod $m 失败（可能已内建/已加载）"
    done
    # 节点默认 0600 root:root：SF/screencap/探针（shell 或非 root）全被 EACCES 挡住
    # （gbmprobe 实测 open errno=13，mapper 因此 NO_RESOURCES）⇒ 放开到 666（binder 同款先例）。
    $BB chmod 666 /dev/dri/card0 /dev/dri/renderD128 2>/dev/null
    $BB ls -la /dev/dri 2>&1 | $BB head -5
else
    echo "[mods] 未注入 /b1/mods.tar.gz（跳过）"
fi

# ── B1.1：Android binder 服务栈 ──
# astack v9 (2026-10-02)：dri 软链 tar 化/SF+adbd 拉起/verify 自检/tmpfs 扩容瘦身/SF 注册轮询后 screencap
if [ -f /b1/android-stack.tar.gz ]; then
    # ⚠ 顺序问题（实测）：本段在 init 里排在 weston 段**之前**，而 composer 服务要连 Wayland
    #    （日志 "WAYLAND_DISPLAY: wayland-0 / Could not open Wayland display / failed to open
    #     wayland connection"）⇒ 这里先引导 weston，并等 socket 就绪再起图形服务。
    [ -x /usr/bin/weston ] || { [ -f /b1/weston.tar.gz ] && $BB tar xzf /b1/weston.tar.gz -C /; }
    # HWC(hwcomposer.waydroid) 会**降权到 uid 1000(system)** 并把 XDG_RUNTIME_DIR 算成
    # /run/user/1000（实测日志 "XDG_RUNTIME_DIR: /run/user/1000"，完全忽略我们的 env）
    # ⇒ weston 也必须以 uid 1000 跑、socket 放 /run/user/1000 才能对上。
    $BB mkdir -p /run/user/0 /run/user/1000 && $BB chmod 700 /run/user/0 /run/user/1000
    $BB chown 1000:1000 /run/user/1000 2>/dev/null
    if [ -x /usr/bin/weston ] && ! $BB pgrep -x weston >/dev/null 2>&1; then
        # busybox 没有 setuidgid ⇒ 用 su + /etc/passwd 条目（AID_SYSTEM=1000）
        $BB grep -q "^system:" /etc/passwd 2>/dev/null || echo "system:x:1000:1000::/:/system/bin/sh" >> /etc/passwd
        $BB grep -q "^system:" /etc/group 2>/dev/null || echo "system:x:1000:" >> /etc/group
        ( $BB su system -c "XDG_RUNTIME_DIR=/run/user/1000 /usr/bin/weston --backend=headless --shell=kiosk --socket=wayland-0 --width=1280 --height=720 " >/tmp/weston.log 2>&1 ) &
        $BB sleep 3
        echo "[astack] weston 已先于图形服务拉起"
    fi
    for i in 1 2 3 4 5 6 7 8 9 10; do [ -S /run/user/1000/wayland-0 ] && break; $BB sleep 1; done
    if [ -S /run/user/1000/wayland-0 ]; then echo "[astack] Wayland socket 已就绪（/run/user/1000/wayland-0）"; else echo "[astack] 警告：wayland-0 未就绪"; fi
    # rootfs 是 tmpfs、默认上限 ~1GB，而基础镜像已占 ~800MB——astack 再解 ~250MB
    # 的库必然 ENOSPC（实测 100% 满：libcamera_client 覆盖失败、screencap 写 0 字节）。
    # 先把 tmpfs 上限扩到 1500M（RAM 共 2.5G），解完包再删 tar 腾回空间。
    $BB mount -o remount,size=1500M / 2>/dev/null && echo "[astack] rootfs 已扩到 1500M"
    $BB tar xzf /b1/android-stack.tar.gz -C / && echo "[astack] 解包完成"
    # binfmt 端到端自测：静态 arm64 → binfmt_misc → qemu-user 转译（2026-10-03）
    $BB ls /binfmt_misc/
    ST=$(/binfmt_test_arm64 2>&1)
    echo "[init] binfmt 自测: $ST"
    # 真属性区：bionic 的 LD_PRELOAD 开关(ro.debuggable) 与 HIDL 的 ready 标记都读它；
    # 必须在任何服务之前建好（proppreload 的 per-process 表顶不了跨进程约定）。
    [ -x /system/bin/propinit ] && /system/bin/propinit 2>&1 | $BB head -8
    # 链接器命名空间：Android 的 libEGL.so loader 用**绝对路径** dlopen("/system/lib64/egl/…")，
    # 而链接器只允许 permitted.paths 里的绝对路径 ⇒ 不补就被拒，loader 回落成内置
    # "Android META-EGL" 空壳（实测 eglQueryString 自述如此），eglChooseConfig 便一个配置都不给，
    # SurfaceFlinger 直接 "no suitable EGLConfig found"。hw/ 是 hw_get_module 找 HAL 用的。
    if [ -f /linkerconfig/ld.config.txt ] && ! $BB grep -q "lib64/egl" /linkerconfig/ld.config.txt; then
        $BB sed -i "s|^namespace.default.permitted.paths = |namespace.default.permitted.paths = /system/lib64/egl:/system/lib64/hw:/vendor/lib64:/vendor/lib64/egl:/vendor/lib64/hw:|" /linkerconfig/ld.config.txt
        echo "[astack] ld.config.txt 已补 egl/hw 的 permitted.paths"
    fi
    # mesa 软件 GL（llvmpipe）：这个 SF 版本没有 CPU 渲染后端，RenderEngine 必须有 EGL。
    [ -f /b1/mesa-gl.tar.gz ] && $BB tar xzf /b1/mesa-gl.tar.gz -C / && echo "[astack] mesa GL 已解包"
    # ── tmpfs 瘦身（2026-10-02，T1 验收链实测）──
    # rootfs（tmpfs，1GB 上限）100% 满 ⇒ astack 解包半途 ENOSPC（libcamera_client 等
    # 覆盖失败、screencap 写不出 PNG）。三个大 tar 解完即删，腾回 ~106MB。
    $BB rm -f /b1/android-stack.tar.gz /b1/mesa-gl.tar.gz /b1/weston.tar.gz
    $BB chmod 644 /system/lib64/*.so 2>/dev/null
    echo "[astack] tmpfs 瘦身: $($BB df -h / 2>/dev/null | $BB tail -1)"
    # mesa 的 DRI 搜索路径：dri/<driver>_dri.so 必须软链到 ../libgallium_dri.so
    # （108 的 Waydroid 容器就是 init 建的；缺了它 gbm_create_device 直接 ENOENT => mapper NO_RESOURCES）
    $BB mkdir -p /system/lib64/dri /vendor/lib64/dri 2>/dev/null
    for _n in swrast llvmpipe iris virtio_gpu kms_swrast zink; do
        $BB ln -sf /system/lib64/libgallium_dri.so /system/lib64/dri/${_n}_dri.so 2>/dev/null
        $BB ln -sf /system/lib64/libgallium_dri.so /vendor/lib64/dri/${_n}_dri.so 2>/dev/null
    done
    $BB cp -f /system/lib64/libgallium_dri.so /vendor/lib64/libgallium_dri.so 2>/dev/null
    # minigbm 的后端插件搜索路径是 /vendor/lib64/：先试 <gpu>_gbm.so 再回退 dri_gbm.so
    # （108 Waydroid 参照系实锤：/vendor/lib64/ 同时有 dri_gbm.so + libgallium_dri.so，
    #   且不存在 virtio_gpu_gbm.so ⇒ 走的就是 dri_gbm.so 回退）——缺了它 gbm_create_device=ENOENT。
    $BB cp -f /system/lib64/dri_gbm.so /vendor/lib64/dri_gbm.so 2>/dev/null
    # screencap 落盘目录：shell(uid 2000) 写不了 /data 根，建 777 目录给它
    # （/data 是启动时重挂的 tmpfs，镜像树里 mk_win 建的 data/local/tmp 不会存活）
    $BB mkdir -p /data/local/tmp
    $BB chmod 777 /data/local/tmp
    # dalvik-cache 同理会被 /data tmpfs 重挂清掉；app_process 起 ART 时要往
    # /data/dalvik-cache/x86_64 写缓存，目录缺失 = 启动即 SIGABRT（2026-10-02 实测）
    $BB mkdir -p /data/dalvik-cache/x86_64
    echo "[astack] dri 目录: $($BB ls /system/lib64/dri 2>/dev/null | $BB tr \"\n\" \" \")"
    $BB mkdir -p /dev/binderfs
    # binderfs 的节点必须用 ioctl(BINDER_CTL_ADD) 创建（挂载本身只给 binder-control）。
    # 为什么必须独立实例：HIDL 注册走 /dev/hwbinder，符号链接到 /dev/binder（同实例）时实测报
    #   Could not get transport for ...IAllocator/default: Status(EX_TRANSACTION_FAILED): BAD_TYPE
    [ -x /system/bin/mkbinders ] && /system/bin/mkbinders 2>&1 | $BB head -8
    for b in binder hwbinder vndbinder; do [ -e /dev/binderfs/$b ] && ln -sf /dev/binderfs/$b /dev/$b; done
    # binderfs 没挂成（或内核未建默认节点）时的回落：同实例符号链接。
    # 严格说 hwbinder 应有独立实例，但至少能让 servicemanager 起来。
    [ -e /dev/hwbinder ] || ln -sf /dev/binder /dev/hwbinder
    [ -e /dev/vndbinder ] || ln -sf /dev/binder /dev/vndbinder
    $BB ls -la /dev/binder /dev/hwbinder /dev/vndbinder 2>&1 | $BB head -6
    # devpts：adb shell / exec-out 需要可用的 pty（实测 screencap 报
    # "failed to create pty master" 就是这个原因，与显示栈无关）。
    $BB mkdir -p /dev/pts
    [ -c /dev/ptmx ] || $BB mknod /dev/ptmx c 5 2 2>/dev/null
    $BB chmod 666 /dev/ptmx 2>/dev/null
    $BB mount -t devpts devpts /dev/pts 2>/dev/null && echo "[astack] devpts 已挂载"
    # binder 节点默认 0600 root ⇒ adb shell(uid=shell) 下跑 screencap 会报
    # "Binder driver /dev/binder could not be opened: Permission denied"（实测）⇒ 放开权限。
    $BB chmod 666 /dev/binder /dev/hwbinder /dev/vndbinder 2>/dev/null
    # 与 108 对齐：vndservicemanager 服务 /dev/vndbinder 上的 vendor HAL（108 在跑）
    VNDSM=/vendor/bin/vndservicemanager; [ -x $VNDSM ] || VNDSM=/vendor/bin/hw/vndservicemanager; if [ -x $VNDSM ]; then
        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true" $VNDSM /dev/vndbinder >/tmp/vndsm.log 2>&1 ) &
        $BB sleep 1
        echo "[astack] vndservicemanager pid=$($BB pidof vndservicemanager 2>/dev/null)"
    fi
    export LD_LIBRARY_PATH=/system/lib64
    ( LD_PRELOAD=/proppreload.so /system/bin/servicemanager >/tmp/sm.log 2>&1 ) &
    ( LD_PRELOAD=/proppreload.so /system/bin/hwservicemanager >/tmp/hsm.log 2>&1 ) &
    $BB sleep 2
    # 图形 HAL 服务：mesa 的 Android EGL 平台初始化需要 mapper@4.0 服务（由 allocator@4.0 提供），
    # SF 需要 composer。rc 里它们的启动条件是 ro.hardware.gralloc=minigbm_gbm_mesa（由 PROPFIX 提供）。
    # configstore：SF 要 configstore@1.0::ISurfaceFlingerConfigs；不注册它会一直
    # 打 "trying to start it as a lazy HAL"（我们没跑 init，没有 lazy HAL 机制）⇒ 必须自己起。
    # 启动顺序：allocator(mapper) 必须先于 composer —— DRM master 唯一，谁先 open /dev/dri/card0
    # 谁是 master；mapper 的 wrapper 要求 master（实测非 master 时 gbm_create_device 返回 EINVAL），
    # composer 晚起也能工作（weston headless 不占 DRM master）。
    for svc in android.hardware.graphics.allocator@4.0-service.minigbm_gbm_mesa android.hardware.configstore@1.1-service android.hardware.graphics.composer@2.1-service; do
        [ -x /vendor/bin/hw/$svc ] || continue
        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX_CRASH=1 PROPFIX_DEBUG=1 PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0;persist.waydroid.width=1280;persist.waydroid.height=720;persist.waydroid.use_subsurface=false;persist.waydroid.multi_windows=false;persist.waydroid.no_presentation=true;persist.waydroid.no_background_subsurface=true;persist.waydroid.cursor_on_subsurface=false;persist.waydroid.cursor_force_shm=true;persist.waydroid.reverse_scrolling=false;persist.waydroid.width_padding=0;persist.waydroid.height_padding=0" LD_LIBRARY_PATH=/system/lib64/egl:/system/lib64/hw:/vendor/lib64/egl:/vendor/lib64/hw:/vendor/lib64:/system/lib64 WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/1000 /vendor/bin/hw/$svc >/tmp/$svc.log 2>&1 ) &
        echo "[astack] 已拉起 $svc"
    done
    # Waydroid 专有 task 服务（system 侧）：SF/HWC 会等它
    for svc in vendor.waydroid.task@1.0-service; do
        [ -x /system/bin/hw/$svc ] || continue
        ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0" LD_LIBRARY_PATH=/system/lib64/egl:/system/lib64/hw:/vendor/lib64:/system/lib64 /system/bin/hw/$svc >/tmp/$svc.log 2>&1 ) &
        echo "[astack] 已拉起 $svc"
    done
    $BB sleep 4
    # 启动顺序：allocator(mapper) 必须先于 composer —— DRM master 唯一，谁先 open /dev/dri/card0
    # 谁是 master；mapper 的 wrapper 要求 master（实测非 master 时 gbm_create_device 返回 EINVAL），
    # composer 晚起也能工作（weston headless 不占 DRM master）。
    for svc in android.hardware.graphics.allocator@4.0-service.minigbm_gbm_mesa android.hardware.configstore@1.1-service android.hardware.graphics.composer@2.1-service; do
        echo "[astack] $svc 日志:"; $BB cat /tmp/$svc.log | $BB head -8
    done
    echo "[astack] hwservicemanager 存活: pid=$($BB pidof hwservicemanager)"
    # EGL 隔离探针：SF 里 EGL 的失败信息被 liblog 吞掉（乱码），这里用独立进程走同一条 EGL 路径，
    # mesa 的 stderr 与每一步的 errno 都直接进控制台。跑完就退出，不影响后续。
    if [ -x /system/bin/eglprobe ]; then
        $BB sleep 2
        LD_PRELOAD=/system/lib64/libLLVM22.so:/system/lib64/libpropfix.so:/proppreload.so \
        PROPFIX="hwservicemanager.ready=true;ro.hardware.egl=angle;ro.hardware.vulkan=lvp;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.hwcomposer=waydroid;gralloc.gbm.device=/dev/dri/card0" \
        GALLIUM_DRIVER=swrast LIBGL_ALWAYS_SOFTWARE=1 MESA_LOADER_DRIVER_OVERRIDE=swrast \
        LD_LIBRARY_PATH=/vendor/lib64/egl:/vendor/lib64:/system/lib64:/system/lib64/egl \
        /system/bin/eglprobe 2>&1 | $BB head -20
    fi
    # GBM 探针：独立复现 minigbm/mesa 的分配，绕开符号拦截拿真实错误
    if [ -x /system/bin/gbmprobe ]; then
        LD_LIBRARY_PATH=/system/lib64:/vendor/lib64 LD_PRELOAD=/system/lib64/libpropfix.so PROPFIX="hwservicemanager.ready=true;ro.hardware.gralloc=minigbm_gbm_mesa;gralloc.gbm.device=/dev/dri/card0" /system/bin/gbmprobe 2>&1 | $BB head -22
    fi
    # 服务清单检查：AIDL 的 SurfaceFlingerAIDL 是否注册（screencap 要靠它）
    if [ -x /system/bin/svccheck ]; then
        # 后台周期查询：SF 由桥(Java)在 init 之后才拉起，单次早查无意义
        ( for i in 1 2 3 4 5 6; do
            $BB sleep 30
            echo "[svccheck] 第 $i 次（约 $((i*30)) 秒）："
            LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX="hwservicemanager.ready=true" /system/bin/svccheck 2>&1 | $BB grep -E "SurfaceFlinger|结束"
          done ) &
    fi
    # HAL 服务注册完成的哨兵：桥（Java）会等它再拉起 SurfaceFlinger。
    # 实测 SF 与 HAL 注册存在竞争：抢跑会撞上间歇性的 gralloc-mapper is missing / libEGL 找不到实现。
    $BB sleep 2
    $BB touch /tmp/hal_ready
    echo "[astack] HAL ready sentinel written to /tmp/hal_ready"
    # ── SF/adbd 由 init 直接拉起（2026-10-02）──
    # 桥内 ProcessBuilder 的 fork 被 proppreload 拦截（EAGAIN，实测 [adbd]/[sf]
    # "Cannot run program ... error=11"）⇒ 移到 init：init 的 sh 不挂 proppreload。
    # 桥侧拉起保留（撞 HAL 注册名起不来，无害）。环境照抄 Server.startSF，
    # 但 gralloc.gbm.device 用 card0（renderD 分配 EACCES，card0 实测成功）。
    # SF 环境与 allocator 同源：gralloc 走 allocator 服务（HAL），SF 自身 EGL=ANGLE(Vulkan lvp)，
    # GALLIUM_* 不影响它 —— 保留原始 swrast 值仅为对齐 Server.startSF 的历史行为。
    ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so PROPFIX_CRASH=1 PROPFIX_DEBUG=1 \
      PROPFIX="hwservicemanager.ready=true;ro.hardware.hwcomposer=waydroid;ro.hardware.gralloc=minigbm_gbm_mesa;ro.hardware.egl=angle;ro.hardware.vulkan=lvp;gralloc.gbm.device=/dev/dri/card0;debug.renderengine.backend=skiagl;ro.surface_flinger.has_wide_color_display=false;ro.surface_flinger.has_HDR_display=false;ro.surface_flinger.use_color_management=false" \
      GALLIUM_DRIVER=swrast MESA_LOADER_DRIVER_OVERRIDE=swrast LIBGL_ALWAYS_SOFTWARE=1 \
      LD_LIBRARY_PATH=/vendor/lib64/egl:/vendor/lib64:/system/lib64:/system/lib64/egl \
      WAYLAND_DISPLAY=wayland-0 XDG_RUNTIME_DIR=/run/user/0 /system/bin/surfaceflinger >/tmp/sf-init.log 2>&1 ) &
    # adbd 必须挂 proppreload（PROPFIX 提供 service.adb.tcp.port 等启动属性），
    # 而 proppreload 的 fork 拦截会弄死 adbd 的 shell 子进程（实测 "fork failed:
    # Try again"）——已在 proppreload.c 按 cmdline 白名单放行 adbd（2026-10-02）。
    # E2 模式曾试过 setprop 走真 init 的 rc adbd：实测 setprop rc=1、rc 服务环
    # 根本没起来（init.svc=0）⇒ 此路不通，两种模式统一自己拉。
    ( LD_PRELOAD=/system/lib64/libpropfix.so:/proppreload.so \
      PROPFIX="hwservicemanager.ready=true;service.adb.tcp.port=5555;service.adb.root=1;ro.adb.secure=0;ro.debuggable=1;persist.adb.tls_server.enable=0" \
      LD_LIBRARY_PATH=/vendor/lib64:/system/lib64 /system/bin/adbd >/tmp/adbd-init.log 2>&1 ) &
    echo "[astack] SF/adbd 已由 init 拉起"
    # 1 秒同步探针：区分「子 shell 没跑/重定向失败」与「进程秒死但日志在」——
    # 若 1 秒后文件不存在，说明 spawn 或重定向本身失败（上一轮 sf-init.log 消失的判别点）。
    $BB sleep 1
    echo "[astack] 启动1秒探针 sf/adbd 日志:"; $BB ls -la /tmp/sf-init.log /tmp/adbd-init.log 2>&1
    # ── 验收自检（无需 adb）：20 秒全量证据 + 35 秒 screencap 出 PNG 魔数 ──
    ( $BB sleep 20; \
      echo "[verify] ===== /tmp 清单 ====="; $BB ls -la /tmp/ 2>&1 | $BB head -24; \
      echo "[verify] ===== 进程清单 ====="; ($BB ps w 2>/dev/null || $BB ps) 2>&1 | $BB grep -E "surfaceflinger|adbd|servicemanager|hwservicemanager|weston|allocator|composer|vndservice" | $BB grep -v grep; \
      echo "[verify] ===== dri 目录 ====="; $BB ls -la /system/lib64/dri/ /vendor/lib64/dri/ 2>&1 | $BB head -24; \
      echo "[verify] ===== 关键文件 ====="; $BB ls -la /system/bin/surfaceflinger /system/bin/adbd /system/bin/screencap /system/bin/svccheck /dev/dri/card0 /dev/dri/renderD128 2>&1; \
      echo "[verify] ===== 内存 ====="; $BB head -2 /proc/meminfo; $BB dmesg 2>/dev/null | $BB tail -6; \
      echo "[verify] ===== sf-init.log 关键行 ====="; $BB grep -aE "allocate|RenderEngine|EGL|eglCreate|dispatcher|AIDL|VINTF|surfaceflinger" /tmp/sf-init.log 2>/dev/null | $BB head -30; echo "[verify] ---- sf-init.log 尾部 ----"; $BB tail -12 /tmp/sf-init.log 2>/dev/null; \
      echo "[verify] ===== adbd-init.log ====="; if [ -f /tmp/adbd-init.log ]; then $BB head -15 /tmp/adbd-init.log; else echo "(不存在)"; fi ) &
    ( $BB sleep 30; \
      # screencap 会卡死在 getService 的无限等待（v8 实测 rc=143 三连）——
      # 必须先轮询到 SF 服务注册完成再跑。SF 注册在部分 boot 上晚于 +30s。
      _OK=0; _N=0; \
      for _i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30; do \
        _N=$_i; \
        if LD_LIBRARY_PATH=/system/lib64 service check SurfaceFlinger 2>/dev/null | $BB grep -q "Service found"; then _OK=1; break; fi; \
        $BB sleep 6; \
      done; \
      echo "[verify] SF 轮询 found=$_OK 第${_N}次"; \
      if [ "$_OK" = 1 ]; then \
        _SC=$(LD_LIBRARY_PATH=/system/lib64 timeout 30 /system/bin/screencap -p /data/local/tmp/shot.png 2>&1); _RC=$?; \
        echo "[verify] SCREENCAP rc=$_RC err=[$_SC] size=$($BB wc -c < /data/local/tmp/shot.png 2>/dev/null) png8=$($BB od -An -tx1 -N8 /data/local/tmp/shot.png 2>/dev/null | $BB tr -d " \n")"; \
      fi ) &
    $BB sleep 3
    echo "[astack] servicemanager pid=$($BB pidof servicemanager 2>/dev/null)"
    echo "[astack] hwservicemanager pid=$($BB pidof hwservicemanager 2>/dev/null)"
    # SurfaceFlinger 不在这里起：必须由桥（Java）起 —— 它要先 set 属性
    # hwservicemanager.ready / ro.hardware.hwcomposer / debug.renderengine.backend，
    # 而 init 脚本没有 setprop（我们的属性服务是 proppreload 假装的）。见 Server.startSurfaceFlinger。
    echo "[astack] sm.log:"; $BB head -4 /tmp/sm.log 2>/dev/null
    echo "[astack] hsm.log:"; $BB head -4 /tmp/hsm.log 2>/dev/null
else
    echo "[astack] 未注入 /b1/android-stack.tar.gz（跳过）"
fi

# ★ 2026-10-03：binfmt 路线的 qemu 环境前缀（必须在 artlaunch 之前 export，桥→jar→pvideo 逐级继承）。
#   GoProxy 子进程（pvideo 等 arm64 二进制）经 binfmt_misc 落到 qemu-aarch64-static，
#   qemu 按 PT_INTERP 找 /system/bin/linker64 —— 在 x86 guest 里那是个 **x86_64** linker
#   → "Invalid ELF image for this architecture" → pvideo 秒退 255 → FishGuard 判篡改 → 桥自毁。
#   QEMU_LD_PREFIX（= -L）只被 qemu-user 读，x86 的 artlaunch/ART 无视它；
#   与上方 LD_LIBRARY_PATH 的「arm64 路径禁入 init 环境」警告不冲突。harness 行已有显式 -L（值相同）。
export QEMU_LD_PREFIX=/thunder-arm

# ★ GoProxy 流隧道（2026-10-03）：pvideo 只绑 127.0.0.1:5266，slirp hostfwd 从 eth0
#   进来会被直接 RST（2026-09-26 实测教训），busybox nc -l 单发又只服一个连接
#   （播放器视频+弹幕并发会卡死 backlog）。tcpfwd（fork per connection，静态自包含）
#   把 0.0.0.0:25266 桥接到 127.0.0.1:5266；宿主经 hostfwd → 25266 → tcpfwd → pvideo。
#   pvideo 未起时连接失败、起来即通，无需时序配合。
(
  while true; do
    /bin/tcpfwd 25266 127.0.0.1 5266 >>/tmp/tcpfwd.log 2>&1
    echo "[tcpfwd] 退出（code=$?），1s 后重启" >> /tmp/tcpfwd.log
    $BB sleep 1
  done
) &

LD_PRELOAD=/proppreload.so /system/bin/artlaunch bridge.GuestMain /gb.dex:/tvbox.apk $PORT &
LP=$!
while true; do
    if ! $BB kill -0 $LP 2>/dev/null; then
        wait $LP
        RC=$?
        echo "[init] 桥进程已退出，退出码=$RC（139=SIGSEGV 132=SIGILL 134=SIGABRT 137=SIGKILL 0/1=主动退出）"
        break
    fi
    $BB sleep 5
done
while true; do $BB sleep 3600; done
