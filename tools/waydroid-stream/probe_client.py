#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CATCLAW/1 简易客户端（T3 自带自测工具，无需 T2 就绪）。

做什么：
  · 握手（CATCLAW/1 → OK w h codec fps）
  · 收视频帧并统计（帧数/字节/实测 fps）；h264 把 AU 追加成 probe_out.h264（可用
    ffmpeg -i probe_out.h264 复核整段），jpeg 逐帧存 probe_frames/frame_NNN.jpg
  · 周期发输入帧验证注入：tap 屏幕中心（down…up）、竖向 swipe、key 3(HOME)
  · 心跳 0x02（5s 一拍）；结束时发 0x00 优雅关闭

用法：
  python3 probe_client.py 10.0.0.108:27183 [--save 30] [--seconds 15] [--no-inject]
"""
import argparse
import os
import socket
import struct
import sys
import threading
import time


def recv_exact(s, n, timeout=15):
    s.settimeout(timeout)
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("对端关闭（已收 %d/%d）" % (len(buf), n))
        buf += chunk
    return buf


FIRST_FRAME_TIMEOUT = 20   # ⚠ 段首帧要 ~5s（编码器初始化）+ reset 防抖，首帧等待放宽到 20s


def send_frame(s, ftype, payload=b""):
    s.sendall(struct.pack("!IB", len(payload), ftype) + payload)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("endpoint", help="host[:port]，缺省端口 27183")
    ap.add_argument("--save", type=int, default=0, help="保存前 N 个视频帧")
    ap.add_argument("--seconds", type=float, default=15, help="收流时长")
    ap.add_argument("--no-inject", action="store_true")
    a = ap.parse_args()

    host, _, port = a.endpoint.partition(":")
    port = int(port or 27183)
    s = socket.create_connection((host, port), timeout=10)
    s.sendall(b"CATCLAW/1\n")
    line = b""
    while b"\n" not in line:
        c = s.recv(1)
        if not c:
            raise SystemExit("握手无响应")
        line += c
    reply = line.decode("ascii", "replace").strip()
    print("服务端:", reply)
    parts = reply.split()
    if parts[0] != "OK":
        raise SystemExit("握手失败")
    w, h, codec, fps = int(parts[1]), int(parts[2]), parts[3], int(parts[4])

    if a.save:
        os.makedirs("probe_frames", exist_ok=True)
    h264_out = open("probe_out.h264", "wb") if codec == "h264" else None

    beat_stop = threading.Event()

    def beats():
        while not beat_stop.is_set():
            try:
                send_frame(s, 0x02, struct.pack("!Q", int(time.time() * 1000)))
            except OSError:
                return
            beat_stop.wait(5.0)

    threading.Thread(target=beats, daemon=True).start()

    frames = 0
    total = 0
    inputs_sent = 0
    t0 = time.time()
    next_inject = t0 + 2.0
    inject_plan = [
        ("tap(中心)", lambda: [
            (0x10, struct.pack("!HHB", w // 2, h // 2, 0)),
            (0x10, struct.pack("!HHB", w // 2, h // 2, 1))]),
        ("swipe(自下而上)", lambda: [
            (0x10, struct.pack("!HHB", w // 2, int(h * 0.8), 0))] +
            [(0x10, struct.pack("!HHB", w // 2, int(h * 0.8) - i * (int(h * 0.6) // 5), 2))
             for i in range(1, 6)] +
            [(0x10, struct.pack("!HHB", w // 2, int(h * 0.2), 1))]),
        ("key(HOME=3)", lambda: [(0x11, struct.pack("!IB", 3, 0))]),
    ]
    plan_i = 0

    try:
        while time.time() - t0 < a.seconds:
            if not a.no_inject and time.time() >= next_inject and plan_i < len(inject_plan):
                name, maker = inject_plan[plan_i]
                for ftype, payload in maker():
                    send_frame(s, ftype, payload)
                    inputs_sent += 1
                print("已注入:", name)
                plan_i += 1
                next_inject = time.time() + 3.0
            hdr = recv_exact(s, 5, timeout=FIRST_FRAME_TIMEOUT if frames == 0 else 10)
            length, ftype = struct.unpack("!IB", hdr)
            payload = recv_exact(s, length, timeout=20) if length else b""
            if ftype == 0x01:
                frames += 1
                total += length
                if h264_out and frames <= 100000:
                    h264_out.write(payload)
                if a.save and frames <= a.save and codec == "jpeg":
                    with open("probe_frames/frame_%03d.jpg" % frames, "wb") as f:
                        f.write(payload)
            elif ftype == 0x02:
                pass
            else:
                print("收到未知帧 type=0x%02x len=%d（跳过）" % (ftype, length))
    finally:
        beat_stop.set()
        try:
            send_frame(s, 0x00)
        except OSError:
            pass
        s.close()
        if h264_out:
            h264_out.close()

    dt = time.time() - t0
    print("── 统计 ──")
    print("时长 %.1fs  视频帧 %d  实测 %.1f fps  共 %.2f MB (codec=%s %dx%d@%d)"
          % (dt, frames, frames / dt if dt else 0, total / 1048576.0, codec, w, h, fps))
    print("发送输入帧 %d 个" % inputs_sent)
    if h264_out:
        print("H.264 流已存 probe_out.h264（%.2f MB），复核: ffmpeg -i probe_out.h264 -frames:v 60 -f null -"
              % (os.path.getsize("probe_out.h264") / 1048576.0))
    if a.save and codec == "jpeg":
        print("JPEG 帧已存 probe_frames/")


if __name__ == "__main__":
    main()
