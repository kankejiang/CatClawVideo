#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""JPEG 中继（N4 台架用的临时服务端）：screenrecord(h264) → ffmpeg → MJPEG，按 CATCLAW/1 发给宿主。

为什么需要它：108 这台 Waydroid 上 `screencap -p` **返回 0 字节**（adb shell 与容器内 root 都一样，
2026-10-02 11:40 实测），所以 T3 的 `--codec jpeg` 源在空转（服务端日志：帧数=92 后不再增长）；
而 `screenrecord --output-format=h264` 正常（27183 上实测 28fps）。
本脚本用**同一条 screenrecord 采集路径**产出真实的安卓画面，只是把编码交给 ffmpeg 出 MJPEG，
好让宿主壳（CatClawVideo.Maui 远程画面页）在 T2 的 H.264 解码器落地之前就能端到端验证。
它是临时件，不是 T3 服务端的替代 —— T3 修好 screencap 后应该直接用自己的 serve.py。

输入注入按 `adb shell input` 做，并把每一条（含坐标）写进日志，作为"位置正确"的一侧证据。

用法（在 108 上）：
  python3 jpeg_relay.py --port 27483 --fps 8 [--device 192.168.240.112:5555]
"""
import argparse
import socket
import struct
import subprocess
import sys
import threading
import time

ap = argparse.ArgumentParser()
ap.add_argument("--port", type=int, default=27483)
ap.add_argument("--fps", type=int, default=8)
ap.add_argument("--device", default="192.168.240.112:5555")
ap.add_argument("--size", default="", help="宽x高，留空则用 adb shell wm size 读")
ap.add_argument("--log", default="/tmp/catclaw-relay.log")
A = ap.parse_args()


def log(msg):
    line = "[%s] %s" % (time.strftime("%H:%M:%S"), msg)
    print(line, flush=True)
    with open(A.log, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def adb_shell(cmd):
    return subprocess.run(["adb", "-s", A.device, "shell"] + cmd,
                          capture_output=True, text=True, timeout=20)


W, H = 1280, 688
if A.size and "x" in A.size:
    W, H = (int(v) for v in A.size.lower().split("x", 1))
else:
    out = adb_shell(["wm", "size"]).stdout
    if "x" in out:
        try:
            W, H = (int(v) for v in out.strip().split(":")[-1].lower().split("x", 1))
        except ValueError:
            pass
log("画面尺寸=%dx%d" % (W, H))

# ── 采集链：adb exec-out screenrecord(h264) | ffmpeg(mjpeg@fps) ────────────
p_adb = subprocess.Popen(
    ["adb", "-s", A.device, "exec-out", "screenrecord", "--bit-rate=6M",
     "--output-format=h264", "-"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
p_ff = subprocess.Popen(
    ["ffmpeg", "-loglevel", "error", "-f", "h264", "-i", "pipe:0", "-an",
     "-vf", "fps=%d" % A.fps, "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "6", "-"],
    stdin=p_adb.stdout, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
p_adb.stdout.close()

_latest = None
_lock = threading.Lock()
stop = threading.Event()


def capture_loop():
    """ffmpeg 的 mjpeg image2pipe 输出是一串 JPEG：按 FFD8…FFD9 切成帧（只保留最新一帧）。"""
    buf = bytearray()
    global _latest
    n = 0
    while not stop.is_set():
        chunk = p_ff.stdout.read(4096)
        if not chunk:
            log("ffmpeg 输出结束（采集链断了）")
            break
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
            frame = bytes(buf[s:e + 2])
            del buf[:e + 2]
            if len(frame) < 200:
                continue
            with _lock:
                _latest = frame
            n += 1
            if n % 100 == 0:
                log("已产出 %d 帧（当前 %dB）" % (n, len(frame)))


threading.Thread(target=capture_loop, daemon=True).start()

# ── 协议 CATCLAW/1：握手 + 帧 + 输入回注 ─────────────────────────────────
srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("0.0.0.0", A.port))
srv.listen(4)
log("监听 :%d codec=jpeg fps=%d（中继，非 T3 正式服务端）" % (A.port, A.fps))

last_input = {}


def send_frame(conn, typ, payload):
    conn.sendall(struct.pack(">I", len(payload)) + bytes([typ]) + payload)


def handle_input(typ, payload, peer):
    """把宿主的输入按 adb shell input 打进安卓，并记日志（坐标是证据的关键）。"""
    try:
        if typ == 0x10:                                  # 触摸：u16 x, u16 y, u8 action
            x, y, action = struct.unpack(">HHB", payload[:5])
            if action == 0:
                last_input[peer] = (x, y)
                log("touch down (%d,%d)" % (x, y))
            elif action == 2:
                last_input[peer + "move"] = (x, y)
            elif action == 1:
                start = last_input.get(peer, (x, y))
                if abs(start[0] - x) + abs(start[1] - y) < 12:
                    adb_shell(["input", "tap", str(x), str(y)])
                    log("touch up → tap (%d,%d)" % (x, y))
                else:
                    adb_shell(["input", "swipe", str(start[0]), str(start[1]),
                               str(x), str(y), "200"])
                    log("touch up → swipe (%d,%d)→(%d,%d)" % (start[0], start[1], x, y))
        elif typ == 0x11:                                # 按键：u32 keycode, u8 action
            code, action = struct.unpack(">IB", payload[:5])
            if action == 0:
                adb_shell(["input", "keyevent", str(code)])
                log("key keycode=%d (%s)" % (code, hex(code)))
        elif typ == 0x12:                                # 滚轮：i16 dx, i16 dy
            dx, dy = struct.unpack(">hh", payload[:4])
            # 中继只做最小可用映射：滚动量大→列表上下键（T3 正式实现用 sendevent）
            key = 20 if dy < 0 else 19
            adb_shell(["input", "keyevent", str(key)])
            log("wheel dy=%d → keycode=%d" % (dy, key))
    except Exception as ex:
        log("输入注入失败：%r" % (ex,))


def serve(conn, peer):
    conn.settimeout(30)
    line = b""
    while not line.endswith(b"\n"):
        d = conn.recv(1)
        if not d:
            return
        line += d
    if line.strip() != b"CATCLAW/1":
        log("握手行不对：%r" % line)
        return
    conn.sendall(("OK %d %d jpeg %d\n" % (W, H, A.fps)).encode())
    log("%s 已握手" % peer)
    conn.settimeout(None)
    while not stop.is_set():
        with _lock:
            frame = _latest
        if frame is None:
            time.sleep(0.05)
            continue
        send_frame(conn, 0x01, frame)
        # 读输入（非阻塞轮询，够 8fps 用）
        try:
            conn.settimeout(0.02)
            hdr = conn.recv(5)
            if len(hdr) == 5:
                ln = int.from_bytes(hdr[:4], "big")
                body = conn.recv(ln) if ln else b""
                if hdr[4] == 0x00:
                    log("%s 主动关闭" % peer)
                    return
                handle_input(hdr[4], body, peer)
        except socket.timeout:
            pass
        except Exception as ex:
            log("%s 断开：%r" % (peer, ex))
            return
        time.sleep(1.0 / max(1, A.fps))


try:
    while True:
        c, addr = srv.accept()
        t = threading.Thread(target=serve, args=(c, "%s:%d" % addr), daemon=True)
        t.start()
except KeyboardInterrupt:
    pass
finally:
    stop.set()
    for p in (p_adb, p_ff):
        try:
            p.terminate()
        except Exception:
            pass
