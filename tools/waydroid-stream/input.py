#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""输入注入：CATCLAW/1 输入帧（0x10 触摸 / 0x11 按键 / 0x12 滚轮）→ Android input 命令。

通道（按优先级，均已实测）：
  1) 持久 `waydroid shell`（stdin 喂命令行）—— 探针实测 `input tap` rc=0，单条延迟低；
     进程死了下一条命令自动重拉。
  2) 回退 `waydroid shell -- input …`（逐条起进程；慢 ~300ms，但最稳）。

为什么不用 scrcpy 控制消息：v1 以"跑通协议 + 可见反应"为先，waydroid shell 是文档
明确允许的注入路线；scrcpy 控制消息注入列为后续增强（见 README「限制」）。

命令行（联调自测用）：
  python3 input.py tap 640 344
  python3 input.py swipe 100 600 900 300 300
  python3 input.py key 4          # BACK
  python3 input.py scroll 240     # 近似：屏幕中心竖向 swipe
"""
import argparse
import shlex
import subprocess
import sys
import threading
import time
from queue import Queue, Full, Empty


def _log(msg):
    sys.stdout.write(time.strftime("[%H:%M:%S] ") + msg + "\n")
    sys.stdout.flush()


class Injector:
    """串行注入器：所有命令经单个 worker 线程按序执行（input 并发会互相踩）。"""

    def __init__(self, device=None, size=None, queue_size=64):
        self.device = device          # 备用（adb 路线时用），waydroid 路线不需要
        self.size = size or (1280, 688)
        self.proc = None              # 持久 waydroid shell
        self.fails = 0
        self.ok = 0
        self.q = Queue(maxsize=queue_size)
        self._stop = False
        self._worker = threading.Thread(target=self._loop, daemon=True, name="injector")
        self._worker.start()

    # ── 对外 ──
    def submit(self, cmd):
        """cmd: list，如 ["input","tap","640","344"]。队列满则丢弃并计数。"""
        try:
            self.q.put_nowait(cmd)
        except Full:
            self.fails += 1

    def stop(self):
        self._stop = True
        try:
            if self.proc and self.proc.stdin:
                self.proc.stdin.write(b"exit\n")
                self.proc.stdin.flush()
        except Exception:
            pass

    # ── 内部 ──
    def _loop(self):
        while not self._stop:
            try:
                cmd = self.q.get(timeout=0.5)
            except Empty:
                continue
            try:
                self._run(cmd)
            except Exception as e:
                _log("[inject] 执行失败 %r: %r" % (cmd, e))
                self.fails += 1

    def _ensure_persistent(self):
        if self.proc is not None and self.proc.poll() is None:
            return True
        try:
            self.proc = subprocess.Popen(
                ["waydroid", "shell"],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, bufsize=0, text=False)
            time.sleep(0.4)   # lxc attach 就绪
            return self.proc.poll() is None
        except Exception:
            self.proc = None
            return False

    def _run(self, cmd):
        line = " ".join(shlex.quote(str(c)) for c in cmd) + "\n"
        with threading.Lock():
            if self._ensure_persistent():
                try:
                    self.proc.stdin.write(line.encode("utf-8"))
                    self.proc.stdin.flush()
                    self.ok += 1
                    return
                except Exception as e:
                    _log("[inject] 持久 shell 写入失败（%r），降级逐条" % e)
                    try:
                        self.proc.kill()
                    except Exception:
                        pass
                    self.proc = None
            # 回退：逐条 `waydroid shell --`（坑⑤：传可执行文件+参数，不经 shell）
            r = subprocess.run(["waydroid", "shell", "--"] + cmd,
                               capture_output=True, text=True, timeout=25)
            if r.returncode != 0:
                raise RuntimeError("waydroid shell -- %s rc=%d: %s"
                                   % (" ".join(cmd), r.returncode, r.stderr[:120]))
            self.ok += 1


# ─────────────────────────── CLI（联调自测）───────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Android 输入注入（Waydroid）")
    ap.add_argument("action", choices=["tap", "swipe", "key", "scroll"])
    ap.add_argument("args", nargs="*")
    a = ap.parse_args()
    inj = Injector()
    if a.action == "tap" and len(a.args) == 2:
        inj.submit(["input", "tap", a.args[0], a.args[1]])
    elif a.action == "swipe" and len(a.args) in (4, 5):
        inj.submit(["input", "swipe"] + a.args)
    elif a.action == "key" and len(a.args) == 1:
        inj.submit(["input", "keyevent", a.args[0]])
    elif a.action == "scroll" and len(a.args) == 1:
        dy = int(a.args[0])
        cx, cy = inj.size[0] // 2, inj.size[1] // 2
        amp = max(-300, min(300, dy))
        inj.submit(["input", "swipe", str(cx), str(cy), str(cx), str(cy - amp), "180"])
    else:
        ap.error("参数不对")
    # 等 worker 执行完
    t0 = time.time()
    while inj.q.unfinished_tasks and time.time() - t0 < 30:
        time.sleep(0.05)
    inj.stop()
    print("ok=%d fails=%d" % (inj.ok, inj.fails))


if __name__ == "__main__":
    main()
