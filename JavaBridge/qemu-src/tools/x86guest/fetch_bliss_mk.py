#!/usr/bin/env python3
"""拉 supremegamers ndk_translation prebuilt 仓库的 mk 配置（Bliss 官方集成参考）"""
import json, base64, urllib.request

BASE = "https://api.github.com/repos/supremegamers/vendor_google_proprietary_ndk_translation-prebuilt/contents/{path}?ref=11arm_13arm64"
FILES = ["libndk_translation.mk", "native_bridge_arm_on_x86.mk", "board/native_bridge_arm_on_x86.mk"]

for f in FILES:
    print("===== %s =====" % f)
    try:
        with urllib.request.urlopen(BASE.format(path=f), timeout=30) as r:
            d = json.loads(r.read().decode())
        print(base64.b64decode(d["content"]).decode())
    except Exception as e:
        print("(拉取失败: %s)" % e)
