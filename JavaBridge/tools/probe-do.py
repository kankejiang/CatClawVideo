#!/usr/bin/env python3
"""桥台架：脱离 App 直接问 JVM 桥「爬虫的 proxy(Map) 对各 do 值答什么」。

为什么需要它（2026-09-24 网盘扫码排查）：宿主 SpiderProxyServer 只把 do=js/config/danmu 转发给
爬虫，而爬虫 `ProxyOrigin.proxy` 的真实 do 词表有 18 个值（字符串是运行时从 short[] 表解密的，
逆向见 tools/DecodeDo.java）。要判断哪个 do 会弹「已登录+启用中」原生对话框/二维码，
不能改生产路由去试（GuardSession 的 Pan.proxyInput 兜底对任何 do 都返回非空 HTML，
一放开就会把 do=m3u8 的取流中继吞成 HTML 页）——所以起一个桥进程按行协议逐个探。

用法::

    python probe-do.py                      # 扫默认 do 词表
    python probe-do.py quark ali input      # 只扫指定值
    python probe-do.py --load-log           # 只打印 load 阶段的日志（不扫）

对话框事件（UiBridge 的 ui-dialog/ui-toast）会即时打出来 —— 那就是扫码/网盘 UI 的信号。
"""
import glob
import json
import os
import queue
import subprocess
import sys
import tempfile
import threading
import time

# Windows 控制台默认 GBK，输出 ✗/⬆ 这类字符会 UnicodeEncodeError
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BRIDGE = r"D:\Code\CatClawVideo\JavaBridge"
CONV = os.path.join(os.environ["APPDATA"], "CatClawVideo.debug", "javabridge", "converted")
HASH = "08a27c1fff2c064ae9a12c34"          # 我的云盘（MyDriveGuard）转换产物
SITE = "probe"
CLASS = "MyDriveGuard"      # 简名即可：桥会自己补 com.github.catvod.spider. 前缀
# ext 必须与订阅里该站的一致：空 ext 会让壳走进与生产不同的分支，撞上 dex2jar 产物的
# VerifyError（merge/Rc 缺 stackmap frame）。取自 sites-cache.json 的 MDrive 条目。
EXT = '{"Cloud-drive":"tvfan/Cloud-drive.txt"}'

# ProxyOrigin.proxy 的 18 路 switch 标签（DecodeDo.java 解出；ck 由宿主自答，不扫）
DO_DEFAULT = ["YCyz", "musicLrc", "yinHe", "quark", "prPic", "input", "danmu", "wasm",
              "m3u8", "hmys", "bili", "pic", "ali", "UC", "dnsPic", "MixDemo", "MixWeb", "config"]


def start_bridge():
    # 桥在生产里由 JavaSpiderRuntime 起，classpath 含 vendor/deps（爬虫依赖）+ vendor/unidbg
    # （Guard 解壳器；load 带 rawJar 时会走 unidbg，缺它必 ClassNotFoundException）
    cp = ";".join([os.path.join(BRIDGE, "bridge.jar")]
                  + glob.glob(os.path.join(BRIDGE, "vendor", "deps", "*.jar"))
                  + glob.glob(os.path.join(BRIDGE, "vendor", "unidbg", "*.jar")))
    work = tempfile.mkdtemp(prefix="probe-do-")
    p = subprocess.Popen(
        ["java", "-Dfile.encoding=UTF-8", "-cp", cp, "bridge.Server"],
        cwd=work, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", bufsize=1)
    out_q, err_q = queue.Queue(), queue.Queue()

    def pump(stream, q):
        for line in stream:
            q.put(line.rstrip("\n"))
        q.put(None)

    threading.Thread(target=pump, args=(p.stdout, out_q), daemon=True).start()
    threading.Thread(target=pump, args=(p.stderr, err_q), daemon=True).start()
    return p, out_q, err_q


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dos = args if args else DO_DEFAULT
    p, out_q, err_q = start_bridge()
    replies = {}

    def drain(timeout=0.0):
        """把已到达的 stdout/stderr 行倒出来；stdout 里的 {"ev":…} 就是 jar 弹的 UI。"""
        t_end = time.time() + timeout
        while True:
            try:
                line = out_q.get_nowait()
            except queue.Empty:
                if time.time() >= t_end:
                    break
                try:
                    line = out_q.get(timeout=0.1)
                except queue.Empty:
                    continue
            if line is None:
                break
            try:
                o = json.loads(line)
            except Exception:
                continue
            if "ev" in o:
                print(f"  ⬆ UI 事件 {o['ev']}: {json.dumps(o, ensure_ascii=False)[:220]}")
            elif "id" in o:
                replies[o["id"]] = o
        while True:
            try:
                e = err_q.get_nowait()
            except queue.Empty:
                break
            if e is None:
                break
            if any(k in e for k in ("[guard]", "[srv]", "[ui]", "Init", "失败", "Exception")):
                print(f"  · {e[:170]}")

    def send(obj):
        p.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        p.stdin.flush()

    def wait(rid, secs):
        t_end = time.time() + secs
        while time.time() < t_end:
            if rid in replies:
                return replies.pop(rid)
            drain(0.2)
        return None

    print("══ load（壳框架模式，不带 guardPort → 桥用 unidbg/已转换 jar）══")
    send({"id": 1, "op": "load", "site": SITE, "className": CLASS, "ext": EXT,
          "jars": [os.path.join(CONV, f"{HASH}-java.jar")],
          "shellJar": os.path.join(CONV, f"{HASH}-shell.jar"),
          "rawJar": os.path.join(CONV, f"raw-{HASH}.jar"),
          "realJar": os.path.join(CONV, f"{HASH}-java.jar")})
    r = wait(1, 120)
    print(f"load → {json.dumps(r, ensure_ascii=False)[:200] if r else '超时'}")
    drain(1.0)
    if "--load-log" in sys.argv:
        p.kill(); return 0
    if not r or not r.get("ok"):
        print("✗ load 失败，后续探测无意义")
        p.kill(); return 1

    print("\n══ 逐个 do 探测 ══")
    if "--home" in sys.argv:
        # 卡片是否自带 action（决定点它该走 spider.action 还是 detailContent）
        send({"id": 2, "op": "call", "site": SITE, "method": "homeContent", "args": [True]})
        r = wait(2, 60)
        txt = json.dumps(r, ensure_ascii=False)
        print(f"homeContent → {len(txt)} 字节")
        with open(os.path.join(tempfile.gettempdir(), "probe-home.json"), "w", encoding="utf-8") as f:
            f.write(txt)
        print(f"  全文已存: {os.path.join(tempfile.gettempdir(), 'probe-home.json')}")
        print("  action 出现次数:", txt.count("action"))
        for frag in [txt[i:i + 200] for i in range(0, min(len(txt), 1200), 200)]:
            print("   ", frag[:200])
        p.kill()
        return 0
    hits = []
    for i, do in enumerate(dos, start=10):
        out_file = os.path.join(tempfile.gettempdir(), f"probe-{do}.bin")
        replies.pop(i, None)
        send({"id": i, "op": "proxy", "site": SITE,
              "query": {"do": do, "url": "0000"}, "outFile": out_file})
        r = wait(i, 25)
        drain(0.3)
        if r is None:
            print(f"  do={do:<10} ⏳ 超时/无响应")
            continue
        if not r.get("ok"):
            print(f"  do={do:<10} ✗ {str(r.get('error'))[:80]}")
            continue
        res = str(r.get("result"))
        size = ""
        if os.path.exists(out_file):
            size = f" {os.path.getsize(out_file)}B"
            with open(out_file, "rb") as f:
                head = f.read(80).decode("utf-8", "replace").replace("\n", " ")
        else:
            head = ""
        mark = ""
        if "html" in res.lower():
            mark = " ←HTML 页"
        elif res.startswith("200"):
            mark = " ←非 HTML，值得细看"
        print(f"  do={do:<10} {res}{size}{mark}  {head[:60]}")
        hits.append((do, res))

    p.stdin.write(json.dumps({"op": "exit"}) + "\n")
    p.stdin.flush()
    time.sleep(0.5)
    p.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
