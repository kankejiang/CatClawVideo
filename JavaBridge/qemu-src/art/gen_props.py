"""从镜像自己的 build.prop + prop.default 生成 proppreload 的属性表（init 读的就是这两份）。"""
import io, re, os

props = {}
for f in ('sys28/build.prop', 'sys28/etc/prop.default'):
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
    'ro.product.cpu.abilist64': 'aarch64', 'ro.product.cpu.abilist32': '',
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

def esc(s):
    return s.replace('\\', '\\\\').replace('"', '\\"')

body = ''.join('    { "%s", "%s" },\n' % (esc(k), esc(v)) for k, v in sorted(props.items()))
io.open('v1/props_gen.h', 'w', encoding='utf-8', newline='').write(
    '/* 自动生成，别手改：gen_props.py 从镜像的 /system/build.prop + /etc/prop.default\n'
    '   + 我们的覆盖项（排在后面，__system_property_find 从后往前扫 ⇒ 覆盖生效）。 */\n'
    'static PV g_props[] = {\n' + body + '};\n')
print("镜像来 %d 条 + 覆盖 %d 条 = %d 条" % (n_img, len(extra), len(props)))
for k in ('ro.build.version.all_codenames', 'ro.build.version.incremental', 'ro.build.id',
          'ro.build.version.release', 'ro.build.fingerprint', 'ro.product.cpu.abilist64'):
    print("  %-34s = %r" % (k, props.get(k, '(没有)')))
