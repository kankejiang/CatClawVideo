"""从镜像自己的 build.prop + prop.default 生成 proppreload 的属性表（init 读的就是这两份）。"""
import io, re, os, sys

# 用法：python gen_props.py [aarch64|x86_64]
#   aarch64（默认）→ v1/props_gen.h    —— 现网 aarch64 guest，行为零变化
#   x86_64          → props_gen_x64.h —— x86 mini guest：追加 nativebridge（libndk）属性组，
#                     让 ART 把 arm64 so 的 dlopen 转给 Google ndk_translation 转译执行
#                     （prebuilts 来自 supremegamers 的 ChromeOS sdk_gphone_x86_64:13 抽取）。
arch = sys.argv[1] if len(sys.argv) > 1 else 'aarch64'

props = {}
# 镜像属性源按架构：aarch64 用 sys28（Android9 dump），x86_64 用 x86sys（Waydroid Android13 rootfs）
mirror_dir = 'x86sys' if arch == 'x86_64' else 'sys28'
for f in (f'{mirror_dir}/build.prop', f'{mirror_dir}/etc/prop.default'):
    if not os.path.exists(f):
        print("缺", f); continue
    for ln in io.open(f, encoding='utf-8', errors='replace'):
        ln = ln.strip()
        if not ln or ln.startswith('#') or '=' not in ln:
            continue
        k, v = ln.split('=', 1)
        k, v = k.strip(), v.strip()
        props[k] = v[:91]
n_img = len(props)

extra = {
    'ro.product.cpu.abi': 'arm64-v8a', 'ro.product.cpu.abilist': 'arm64-v8a',
    # ⚠ 必须是 arm64-v8a：这是 Android 的 ABI 名，aarch64 是内核/DTS 名。
# 写错的后果（2026-09-26 装机版实测）：jar 按 abilist64 里找 "arm64" 找不到，
# 就去 load 32 位的 v7 壳 → dlopen failed: "...libFishGuard-v7-....so" is 32-bit instead of 64-bit。
'ro.product.cpu.abilist64': 'arm64-v8a', 'ro.product.cpu.abilist32': '',
    'ro.dalvik.vm.native.bridge': '0', 'dalvik.vm.isa.arm64.variant': 'generic',
    'dalvik.vm.isa.arm64.features': 'default', 'persist.sys.dalvik.vm.lib.2': 'libart.so',
    'ro.zygote': 'zygote64_32', 'dalvik.vm.stack-trace-dir': '/data/anr',
    'dalvik.vm.appimageformat': 'lz4', 'ro.config.nocheckin': '1', 'ro.adb.secure': '0',
    'ro.debuggable': '1', 'persist.sys.timezone': 'Asia/Shanghai', 'net.dns1': '10.0.2.3',
    'net.dns2': '8.8.8.8', 'net.hostname': 'android', 'wifi.interface': 'wlan0',
    'ro.boot.hardware': 'ranchu', 'ro.hardware': 'ranchu', 'qemu.hw.mainkeys': '0',
    'ro.kernel.qemu': '1', 'ro.setupwizard.mode': 'OPTIONAL',
    'ro.product.first_api_level': '28', 'ro.board.platform': '',
    'ro.sf.lcd_density': '420', 'qemu.sf.lcd_density': '420', 'qemu.sf.device_state': 'default',
}
props.update(extra)

if arch == 'x86_64':
    # nativebridge 属性组（x86 mini guest 专属）：arm64 so 的加载交给 libndk_translation。
    # abilist 把 arm64-v8a 排在 x86_64 之后：优先原生，arm64 走转译。32 位不转译（abilist32 空）。
    props.update({
        'ro.enable.native.bridge.exec': '1',
        'ro.dalvik.vm.isa.arm': 'x86',
        'ro.dalvik.vm.isa.arm64': 'x86_64',
        'ro.dalvik.vm.native.bridge': 'libndk_translation.so',
        # ndk_translation.mk 要求的完整属性集（2026-09-27 对着 supremegamers 包补齐）：
        # vendor 开关对 64 位初始化路径、version/flags 是 ndk 行为开关（缺失 = initialize 静默失败）
        'ro.vendor.enable.native.bridge.exec': '1',
        'ro.vendor.enable.native.bridge.exec64': '1',
        'ro.ndk_translation.version': '0.2.3',
        'ro.ndk_translation.flags': 'accurate-sigsegv',
        'ro.product.cpu.abi': 'x86_64',
        'ro.product.cpu.abilist': 'x86_64,arm64-v8a',
        'ro.product.cpu.abilist64': 'x86_64,arm64-v8a',
    })

def esc(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')

body = ''.join('    { "%s", "%s" },\n' % (esc(k), esc(v)) for k, v in sorted(props.items()))
out = 'props_gen_x64.h' if arch == 'x86_64' else 'v1/props_gen.h'
io.open(out, 'w', encoding='utf-8', newline='').write(
    '/* 自动生成，别手改：gen_props.py 从镜像的 /system/build.prop + /etc/prop.default\n'
    '   + 我们的覆盖项（排在后面，__system_property_find 从后往前扫 ⇒ 覆盖生效）。 */\n'
    'static PV g_props[] = {\n' + body + '};\n')
print("镜像来 %d 条 + 覆盖 %d 条 = %d 条" % (n_img, len(extra), len(props)))
for k in ('ro.build.version.all_codenames', 'ro.build.version.incremental', 'ro.build.id',
          'ro.build.version.release', 'ro.build.fingerprint', 'ro.product.cpu.abilist64'):
    print("  %-34s = %r" % (k, props.get(k, '(没有)')))
