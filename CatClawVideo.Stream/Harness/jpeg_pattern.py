#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CATCLAW/1 服务端（N4 宿主壳验收用）：画面 = ffmpeg 测试图案，输入 = 真打进安卓。

为什么这样搭：108 这台 Waydroid 现在拿不到可发的 JPEG 画面 ——
  · `screencap -p` 返回 0 字节（adb shell uid=2000 与容器内 root 都一样，rc=1 且无 stderr）；
  · `screenrecord` 的第 3 个会话拿不到字节（27183/27283 已各占一个编码会话，实测 6s 内 0 字节）。
所以画面侧用 ffmpeg 的 testsrc2 图案（协议、帧率、延迟、丢帧、带宽都是真的），
输入侧**照 T3 的做法打进真安卓**（adb shell input），这样"位置正确 + 安卓真收到"仍可独立取证。
它是宿主壳的验收台架，不是 T3 服务端的替代。

用法（108 上）：python3 jpeg_pattern.py --port 27383 --fps 15
"""
import argparse
import socket
import struct
import subprocess
import threading
import time

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=27383)
ap.add_argument("--fps", type=int, default=15)
ap.add_argument("--device", default="192.168.240.112:5555")
ap.add_argument("--size", default="1280x688")
ap.add_argument("--log", default="/tmp/catclaw-pattern.log")
A = ap.parse_args()
W, H = (int(v) for v in A.size.split("x"))


def log(msg):
    line = "[%s] %s" % (time.strftime("%H:%M:%S"), msg)
    print(line, flush=True)
    with open(A.log, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def adb_shell(args):
    return subprocess.run(["adb", "-s", A.device, "shell"] + args, capture_output=True, text=True, timeout=20)


p_ff = subprocess.Popen(
    ["ffmpeg", "-loglevel", "error", "-f", "lavfi",
     "-re", "-i", "testsrc2=size=%s:rate=%d" % (A.size, A.fps),
     "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "8", "-"],
    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, stdin=subprocess.DEVNULL)

_latest = None
_lock = threading.Lock()
_frames = [0]


def capture():
    buf = bytearray()
    while True:
        chunk = p_ff.stdout.read(4096)
        if not chunk:
            log("ffmpeg 结束")
            return
        buf += chunk
        while True:
            s = buf.find(b"\xff\xd8")
            if s < 0:
                buf.clear()
                break
            e = buf.find(b"\xff\xd9", s + 2)
            if e < 0:
                del buf[:s]
                break
            fr = bytes(buf[s:e + 2])
            del buf[:e + 2]
            if len(fr) < 300:
                continue
            with _lock:
                globals()["_latest"] = fr
            _frames[0] += 1
            if _frames[0] % 3000 == 0:
                log("已产出 %d 帧（%dB）" % (_frames[0], len(fr)))


threading.Thread(target=capture, daemon=True).start()

down = {}


def handle_input(typ, payload, peer):
    if typ == 0x10 and len(payload) >= 5:
        x, y, action = struct.unpack(">HHB", payload[:5])
        if action == 0:
            down[peer] = (x, y)
        elif action == 1:
            sx, sy = down.get(peer, (x, y))
            if abs(sx - x) + abs(sy - y) < 12:
                adb_shell(["input", "tap", str(x), str(y)])
                log("%s tap(%d,%d) 已注入安卓" % (peer, x, y))
            else:
                adb_shell(["input", "swipe", str(sx), str(sy), str(x), str(y), "220"])
                log("%s swipe(%d,%d)→(%d,%d) 已注入安卓" % (peer, sx, sy, x, y))
    elif typ == 0x11 and len(payload) >= 5:
        code, action = struct.unpack(">IB", payload[:5])
        if action == 0:
            adb_shell(["input", "keyevent", str(code)])
            log("%s keyevent %d 已注入安卓" % (peer, code))
    elif typ == 0x12 and len(payload) >= 4:
        dx, dy = struct.unpack(">hh", payload[:4])
        adb_shell(["input", "keyevent", "20" if dy < 0 else "19"])
        log("%s wheel dy=%d → keycode=%d" % (peer, dy, 20 if dy < 0 else 19))


def serve(conn, peer):
    line = b""
    while not line.endswith(b"\n"):
        d = conn.recv(1)
        if not d:
            return
        line += d
    if line.strip() != b"CATCLAW/1":
        log("握手行不对 %r" % line)
        return
    conn.sendall(("OK %d %d jpeg %d\n" % (W, H, A.fps)).encode())
    log("%s 已握手" % peer)
    while True:
        with _lock:
            fr = _latest
        if fr is None:
            time.sleep(0.1)
            continue
        try:
            conn.sendall(struct.pack(">I", len(fr)) + b"\x01" + fr)
            conn.settimeout(1.0 / A.fps)
            hdr = conn.recv(5)
            if len(hdr) == 5:
                ln = int.from_bytes(hdr[:4], "big")
                body = conn.recv(ln) if ln else b""
                if hdr[4] == 0x00:
                    log("%s 关闭" % peer)
                    return
                handle_input(hdr[4], body, peer)
        except socket.timeout:
            pass
        except Exception as ex:
            log("%s 断开 %r" % (peer, ex))
            return


srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("0.0.0.0", A.port))
srv.listen(4)
log("监听 :%d jpeg %dx%d@%d（画面=ffmpeg 图案，输入=真安卓）" % (A.port, W, H, A.fps))
try:
    while True:
        c, addr = srv.accept()
        threading.Thread(target=serve, args=(c, "%s:%d" % addr), daemon=True).start()
except KeyboardInterrupt:
    pass
