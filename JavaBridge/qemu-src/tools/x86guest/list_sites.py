#!/usr/bin/env python3
"""列出宿主 sites-cache.json 里的站点（ASCII 转义输出，避开控制台编码）。"""
import json
import os

p = os.path.join(os.environ["APPDATA"], "CatClawVideo.debug", "sites-cache.json")
d = json.load(open(p, encoding="utf-8"))
print("共 %d 站" % len(d))
for s in d:
    api = str(s.get("Api", ""))
    jar = str(s.get("Jar", "") or "")
    if any(k in api + jar + str(s.get("Name", "")) for k in ("MDrive", "mDrive", "seed", "Seed", "woGG", "WoGG", "WoGGGuard", "荐片", "玩偶")):
        print(json.dumps({
            "Name": s.get("Name"), "Key": s.get("Key"), "Api": api,
            "Jar": jar[-40:], "Ext": str(s.get("Ext", ""))[:60],
            "Playable": s.get("Playable"),
        }, ensure_ascii=True))
print("--- 全部 Api 一览（Playable 的）---")
for s in d:
    if s.get("Playable"):
        print(json.dumps({"Key": s.get("Key"), "Api": str(s.get("Api", ""))[:60]}, ensure_ascii=True))
