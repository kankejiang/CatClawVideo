#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""CATCLAW/1 投屏服务端（Android 侧：108 的 Waydroid，未来可原样搬进 T1 的 guest）。

协议（docs/tasks/README.md 第二节，照实现）：
  1) 客户端连上发一行 "CATCLAW/1\\n"
  2) 服务端回 "OK <width> <height> <codec> <fps>\\n"（codec ∈ {h264, jpeg}）
  3) 双向长度前缀帧 <4B 大端长度><1B 类型><payload>：
     0x01 视频帧 / 0x02 心跳(8B ms) / 0x10 触摸(5B) / 0x11 按键(5B) / 0x12 滚轮(4B)
     单帧上限 4MiB；未知类型按长度跳过；0x00 关闭。

视频源（--source）：
  screenrecord  adb exec-out `screenrecord --output-format=h264`（原生 Annex-B，
                2026-10-01 实测 ffmpeg 解码通过）——H.264 默认路线。注：设备端
                screenrecord 单段上限 180s，此处自动续录（每次重启有 ~0.3s 间隙）。
                文档建议的 scrcpy-server 转发留作 --source scrcpy 的后续增强
                （客户端协议 v4.1 复杂，screenrecord 同样给出 H.264 且少一层协议）。
  jpeg          兜底：`screencap -p` 循环 → 常驻 ffmpeg 转 MJPEG（10~15fps 可接受）。

输入注入（input.py）：0x10/0x11/0x12 → 持久 `waydroid shell`（喂 stdin，实测可用）；
  touch down/move/up 合成 tap/swipe（move 以 ~80ms 间隔注入中间 swipe 保证拖动可见）。

用法：
  python3 serve.py [--port 27183] [--codec auto|h264|jpeg] [--source auto|screenrecord|jpeg]
                   [--fps 30] [--device auto] [--bit-rate 8M]
多客户端：采集源全局单例，向所有已连接客户端扇出（慢客户端丢帧不阻塞采集）。
"""
import argparse
import os
import queue
import select
import socket
import struct
import subprocess
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import input as input_inj  # noqa: E402  （input.py 同目录）

MAX_FRAME = 4 * 1024 * 1024          # 协议单帧上限
HELLO = b"CATCLAW/1"
LOG_PATH = "/tmp/catclaw-stream.log"

_log_lock = threading.Lock()
_log_fp = None


def log(msg):
    line = time.strftime("[%H:%M:%S] ") + msg
    global _log_fp
    with _log_lock:
        try:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()
        except Exception:
            pass
        try:
            if _log_fp is None:
                _log_fp = open(LOG_PATH, "a", encoding="utf-8")
            _log_fp.write(line + "\n")
            _log_fp.flush()
        except Exception:
            pass


# ─────────────────────────── adb 通用 ───────────────────────────

def adb_devices():
    out = subprocess.run(["adb", "devices"], capture_output=True, text=True, timeout=10).stdout
    return [l.split("\t")[0] for l in out.splitlines() if l.endswith("device")]


def pick_device(want):
    devs = adb_devices()
    if want and want != "auto":
        if want in devs:
            return want
        subprocess.run(["adb", "connect", want], capture_output=True, timeout=10)
        devs = adb_devices()
        if want in devs:
            return want
        raise SystemExit("设备 %s 不可用（现有: %s）" % (want, devs))
    if not devs:
        raise SystemExit("无 adb 设备；先 waydroid session start / show-full-ui")
    return devs[0]


def adb_out(device, args, timeout=None, err_path=None):
    """adb exec-out（二进制安全），返回 stdout 字节流（Popen）。
    err_path 给出时 stderr 落盘（screenrecord 起失败的报错全在 stderr，丢掉=瞎调）。"""
    err = open(err_path, "wb") if err_path else subprocess.DEVNULL
    try:
        return subprocess.Popen(
            ["adb", "-s", device, "exec-out"] + args,
            stdout=subprocess.PIPE, stderr=err, bufsize=0)
    finally:
        if err_path and err and err is not subprocess.DEVNULL:
            err.close()


def adb_shell(device, args, timeout=15):
    return subprocess.run(["adb", "-s", device, "shell"] + args,
                          capture_output=True, text=True, timeout=timeout).stdout


def wm_size(device):
    """取当前分辨率（Override 优先——那是实际生效值），返回 (w, h)。"""
    out = adb_shell(device, ["wm", "size"])
    w = h = None
    for line in out.splitlines():
        line = line.strip()
        if line.startswith("Physical size:"):
            w, h = [int(v) for v in line.split(":", 1)[1].strip().split("x")]
        elif line.startswith("Override size:"):
            w, h = [int(v) for v in line.split(":", 1)[1].strip().split("x")]
    if not (w and h):
        raise SystemExit("wm size 解析失败: %r" % out)
    return w, h


# ─────────────────────── H.264 Annex-B 切分 ───────────────────────

def _find_start_codes(buf, pos=0):
    """返回 buf 中所有起始码(000001 / 00000001)的 (起始位置, 码后位置) 列表。"""
    out = []
    n = len(buf)
    i = pos
    while i < n - 3:
        if buf[i] == 0 and buf[i + 1] == 0:
            if buf[i + 2] == 1:
                out.append((i, i + 3))
                i += 3
                continue
            if i < n - 4 and buf[i + 2] == 0 and buf[i + 3] == 1:
                out.append((i, i + 4))
                i += 4
                continue
        i += 1
    return out


def _first_mb_in_slice(nal):
    """解析 slice 头的 first_mb_in_slice（ue(v)）。返回 None=解析不了。
    ⚠ 忽略 emulation prevention（EPB）：只读头几个 bit，撞上 EPB 的概率可忽略。"""
    data = nal[1:6]                   # 跳过 1 字节 NAL 头，取后续 5 字节足够
    if not data:
        return None
    pos = 0
    zeros = 0
    while pos < len(data) * 8:
        bit = (data[pos >> 3] >> (7 - (pos & 7))) & 1
        if bit:
            break
        zeros += 1
        pos += 1
        if zeros > 31:
            return None
    val = 0
    for _ in range(zeros + 1):
        if pos >> 3 >= len(data):
            return None
        bit = (data[pos >> 3] >> (7 - (pos & 7))) & 1
        val = (val << 1) | bit
        pos += 1
    return val - 1


class H264AUAssembler:
    """把 Annex-B 字节流切成访问单元（一帧一 AU）。

    规则：SPS/PPS/SEI/AUD 属「前缀」，挂到下一个切片；切片 NAL 用 slice 头的
    first_mb_in_slice 判帧界（=0 是新帧）—— ⚠ 不能用「遇到新切片就切」：
    -tune zerolatency 会开 sliced-threads（一帧多个 slice，实测 222fps 的碎 AU
    就是这么来的，2026-10-01）。
    """

    PARAM_TYPES = {7, 8, 6, 9}       # SPS / PPS / SEI / AUD
    SLICE_TYPES = {1, 5}             # 非 IDR / IDR

    def __init__(self, emit):
        self.emit = emit
        self.buf = bytearray()
        self.prefix = []              # 挂给下一帧的参数集 NAL（含起始码）
        self.cur = []                 # 当前 AU 的 NAL
        self.have_slice = False

    def feed(self, data):
        self.buf += data
        codes = _find_start_codes(self.buf)
        # 至少要两个起始码才能定界一个**完整** NAL（最后一个 NAL 的结束未知，留在缓冲）
        if len(codes) < 2:
            if len(self.buf) > 8 * 1024 * 1024:
                self.buf.clear()
            return
        for i in range(len(codes) - 1):
            s, e = codes[i]
            nstart = codes[i + 1][0]
            end = nstart
            while end > e and self.buf[end - 1] == 0:   # 去掉属于下一起始码的 0 填充
                end -= 1
            nal = bytes(self.buf[e:end])
            code = bytes(self.buf[s:e])
            if not nal:
                continue
            ntype = nal[0] & 0x1F
            if ntype in self.PARAM_TYPES:
                if self.cur:
                    self._flush()
                self.prefix.append(code + nal)
            elif ntype in self.SLICE_TYPES:
                fmb = _first_mb_in_slice(nal)
                new_frame = (fmb == 0) or (fmb is None and not self.have_slice)
                if self.cur and new_frame:
                    self._flush()
                if not self.cur:
                    self.cur = list(self.prefix)
                    self.prefix = []
                self.cur.append(code + nal)
                self.have_slice = True
            else:
                # 其它 NAL（如 12/filler）：并入当前 AU
                if not self.cur:
                    self.cur = list(self.prefix)
                    self.prefix = []
                self.cur.append(code + nal)
        # 最后一个起始码开始的字节留待下一轮（NAL 未完）
        del self.buf[:codes[-1][0]]

    def _flush(self):
        if self.cur:
            self.emit(b"".join(self.cur))
        self.cur = []
        self.have_slice = False

    def finish(self):
        if self.prefix and not self.cur:
            pass
        self._flush()


# ─────────────────────────── 采集源 ───────────────────────────

class Broadcaster:
    """一份流扇出给 N 个客户端；慢客户端丢帧，绝不阻塞采集。"""

    def __init__(self):
        self.clients = []
        self.lock = threading.Lock()
        self.dropped = 0
        self.sent = 0

    def add(self, client):
        with self.lock:
            self.clients.append(client)

    def remove(self, client):
        with self.lock:
            if client in self.clients:
                self.clients.remove(client)

    def publish(self, payload):
        with self.lock:
            cl = list(self.clients)
        if cl:
            self.sent += 1
        for c in cl:
            try:
                c.send_video(payload)
            except Exception:
                self.remove(c)


class BaseSource(threading.Thread):
    def __init__(self, device, broadcaster, name):
        super().__init__(daemon=True, name=name)
        self.device = device
        self.b = broadcaster
        self.stop_flag = False
        self.frames = 0
        self.started_at = time.time()

    def stop(self):
        self.stop_flag = True

    def request_reset(self):
        """接入新客户端时让 H.264 段重开（新段自带 SPS/PPS+IDR）。JPEG 源无需。"""

    def publish(self, payload):
        self.frames += 1
        self.b.publish(payload)

    def fps(self):
        dt = time.time() - self.started_at
        return self.frames / dt if dt > 0 else 0.0


class ScreenrecordSource(BaseSource):
    """设备端 screenrecord（VFR、静止画面零帧）→ 宿主 ffmpeg 转码 → CFR H.264 扇出。

    为什么加一级宿主转码（2026-10-01 实测决策）：
    · screenrecord 纯 VFR：画面完全静止时连第一帧都不出（实测 50s），验收命令
      `ffmpeg -i tcp://… -frames:v 60` 会在静止桌面上挂死；
    · 段首帧要 ~5s（adb+虚拟显示+编码器初始化），且 reset 会打断所有在线客户端；
    · 宿主 ffmpeg `fps=30`（CFR，静止时自动复制帧）+ `-g 30`（每秒 IDR）一并解决：
      恒定 30fps、后接入者 ≤1s 拿到 IDR、**不再需要任何 reset 逻辑**。
    """

    def __init__(self, device, broadcaster, bit_rate="8M", overlay=True, fps=30):
        super().__init__(device, broadcaster, "screenrecord")
        self.bit_rate = bit_rate
        self.overlay = overlay
        self.target_fps = fps    # ⚠ 不能叫 self.fps：会遮蔽 BaseSource.fps() 方法（stats 崩）
        self._proc = None
        self._ff = None
        self.pump_bytes = 0      # 设备流经 pump 的字节数（诊断用）

    def _pump(self):
        """adb stdout → ffmpeg stdin（独立线程；EOF/断开时收尾）。"""
        p_adb, p_ff = self._p_adb, self._p_ff
        waited = 0
        try:
            while True:
                r, _, _ = select.select([p_adb.stdout], [], [], 5.0)
                if not r:
                    waited += 5
                    log("[pump] %ds 无设备数据（pump=%dB）—— 设备侧 screenrecord 没出流"
                        % (waited, self.pump_bytes))
                    continue
                waited = 0
                chunk = p_adb.stdout.read(65536)
                if not chunk:
                    log("[pump] adb EOF（累计 %dB）" % self.pump_bytes)
                    break
                self.pump_bytes += len(chunk)
                if self.pump_bytes == len(chunk):
                    log("[pump] 设备首块 %dB" % len(chunk))
                p_ff.stdin.write(chunk)
                p_ff.stdin.flush()
        except Exception as e:
            log("[pump] 断开: %r（累计 %dB）" % (e, self.pump_bytes))
        finally:
            try:
                p_ff.stdin.close()
            except Exception:
                pass

    def run(self):
        while not self.stop_flag:
            log("[src] screenrecord 启动（%s，bit-rate=%s，overlay=%s）"
                % (self.device, self.bit_rate, self.overlay))
            self.started_at = time.time()
            self.frames = 0
            seg_err = "/tmp/catclaw-screenrecord.err"
            p_adb = p_ff = None
            try:
                # 杀设备侧残留（kill 宿主侧 adb 后，设备端要等下一次写帧才退）
                subprocess.run(["adb", "-s", self.device, "shell", "pkill screenrecord"],
                               capture_output=True, timeout=8)
                time.sleep(1.0)
                sr_args = ["screenrecord", "--output-format=h264",
                           "--time-limit", "170", "--bit-rate", self.bit_rate]
                if self.overlay:
                    sr_args.append("--bugreport")   # 时间戳覆盖层：强制持续出帧（VFR 对策）
                sr_args.append("-")
                p_adb = adb_out(self.device, sr_args, err_path=seg_err)
                p_ff = self._ffmpeg_cfr()
                self._p_adb, self._p_ff = p_adb, p_ff
                log("[src] 已 spawn adb(pid=%d) ffmpeg(pid=%d)" % (p_adb.pid, p_ff.pid))
                pump = threading.Thread(target=self._pump, daemon=True, name="src-pump")
                pump.start()
                asm = H264AUAssembler(self._on_au)
                while not self.stop_flag:
                    chunk = p_ff.stdout.read(65536)   # FileIO：单次 read 系统调用
                    if not chunk:
                        break
                    asm.feed(chunk)
            except Exception as e:
                log("[src] screenrecord 异常: %r" % e)
            finally:
                try:
                    asm.finish()
                except Exception:
                    pass
                for q in (p_adb, p_ff):
                    try:
                        if q:
                            q.kill()
                    except Exception:
                        pass
                self._proc = None
            if self.stop_flag:
                break
            log("[src] 段结束（170s 上限/异常），0.3s 后续录；本段 %d 帧" % self.frames)
            time.sleep(0.3)

    @staticmethod
    def _ffmpeg_cfr():
        """设备 VFR 流 → 宿主 CFR 30fps H.264（静止时复制帧；-g 30 → 每秒 IDR）。
        ⚠ probesize/analyzeduration 必须压到最小：默认探测要攒 5MB 输入才开工，
        设备 VFR 流只有 ~12KB/s（静止更少），等于永久卡在探测（2026-10-01 实测）。"""
        return subprocess.Popen(
            ["ffmpeg", "-nostdin", "-v", "error", "-probesize", "32",
             "-analyzeduration", "0", "-fflags", "+genpts", "-f", "h264", "-i", "-",
             "-vf", "fps=30", "-c:v", "libx264", "-preset", "ultrafast",
             "-tune", "zerolatency", "-g", "30", "-pix_fmt", "yuv420p",
             "-f", "h264", "-"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=open("/tmp/catclaw-cfr.err", "ab"), bufsize=0)

    def _on_au(self, au):
        self.publish(au)


class ScreencapSource(BaseSource):
    """screencap -p 循环 → 常驻 ffmpeg → h264(CFR) 或 mjpeg 扇出。

    为什么这是默认源（2026-10-01 实测决策）：screenrecord 是纯 VFR——画面完全静止时
    连第一帧都不出（实测等 50s），ffmpeg 的 find_stream_info/fps 滤镜又需要持续输入
    才出流，两者叠加 = 静止桌面永远黑屏。screencap 不依赖画面变化（静止屏幕也出帧），
    配宿主 fps=30 CFR 转码：恒定 30fps 输出（静止时复制帧）、-g 30 每秒 IDR
    （后接入的解码器 ≤1s 入手）。代价：抓帧 ~2-3fps → 内容刷新低但流恒定（README 注明）。
    """

    def __init__(self, device, broadcaster, out_codec="jpeg", fps=30, cap_interval=0.4):
        super().__init__(device, broadcaster, "screencap")
        self.out_codec = out_codec
        self.target_fps = fps          # ⚠ 不能叫 self.fps：会遮蔽 BaseSource.fps() 方法
        self.interval = 1.0 / max(fps, 1)
        self.cap_interval = cap_interval
        self.head_au = None            # 最近的 SPS/PPS+IDR 帧（新客户端接入时先发）

    def _ffmpeg(self):
        if self.out_codec == "h264":
            return subprocess.Popen(
                ["ffmpeg", "-nostdin", "-v", "error",
                 "-f", "image2pipe", "-framerate", "5", "-i", "-",
                 "-vf", "fps=%d" % self.target_fps,
                 "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                 "-g", str(self.target_fps), "-pix_fmt", "yuv420p", "-f", "h264", "-"],
                stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=open("/tmp/catclaw-cfr.err", "ab"), bufsize=0)
        return subprocess.Popen(
            ["ffmpeg", "-nostdin", "-v", "error", "-f", "image2pipe", "-framerate", "5",
             "-i", "-", "-c:v", "mjpeg", "-q:v", "5", "-f", "mjpeg", "-"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            bufsize=0)

    @staticmethod
    def _scan_jpegs(data):
        """从字节流切 JPEG（SOI=FFD8，EOI=FFD9）。返回 (帧列表, 未定界尾部)。"""
        out = []
        pos = 0
        while True:
            soi = data.find(b"\xff\xd8", pos)
            if soi < 0:
                break
            eoi = data.find(b"\xff\xd9", soi + 2)
            if eoi < 0:
                break
            out.append(data[soi:eoi + 2])
            pos = eoi + 2
        return out, data[pos:]

    def run(self):
        while not self.stop_flag:
            log("[src] screencap 源启动（%s → %s，目标 %dfps）"
                % (self.device, self.out_codec, self.target_fps))
            ff = None
            try:
                ff = self._ffmpeg()
                os.set_blocking(ff.stdout.fileno(), False)   # 非阻塞排空
                if self.out_codec == "h264":
                    asm = H264AUAssembler(self._on_au)
                else:
                    asm = None
                tail = b""
                while not self.stop_flag:
                    t0 = time.time()
                    png = adb_shell_bin(self.device, ["screencap", "-p"])
                    if png:
                        try:
                            ff.stdin.write(png)
                            ff.stdin.flush()
                        except Exception as e:
                            log("[src] ffmpeg 管道断开: %r，重启转换器" % e)
                            try:
                                ff.kill()
                            except Exception:
                                pass
                            ff = self._ffmpeg()
                            os.set_blocking(ff.stdout.fileno(), False)
                            tail = b""
                            if asm is not None:
                                asm = H264AUAssembler(self._on_au)
                            continue
                    time.sleep(0.10)     # 给 ffmpeg 转码时间
                    got = b""
                    while True:
                        try:
                            chunk = ff.stdout.read(262144)
                        except BlockingIOError:
                            break
                        if not chunk:
                            break
                        got += chunk
                        if len(got) > 4 * 1024 * 1024:
                            break
                    if got:
                        if asm is not None:
                            asm.feed(got)
                        else:
                            jpegs, tail = self._scan_jpegs(tail + got)
                            for jp in jpegs:
                                self.publish(jp)
                    dt = time.time() - t0
                    time.sleep(max(0.0, self.interval - dt))
            except Exception as e:
                log("[src] screencap 源异常: %r" % e)
            finally:
                if ff:
                    try:
                        ff.kill()
                    except Exception:
                        pass
            if self.stop_flag:
                break
            time.sleep(1.0)

    def _on_au(self, au):
        # 记录「SPS/PPS+IDR」帧：x264 只在首个 IDR 前发参数集（不 repeat），
        # head_au 一旦定住就不变 —— 新客户端接入时先发它，解码器立即有可解起点。
        codes = _find_start_codes(au)
        has_param = has_idr = False
        for i, (s, e) in enumerate(codes):
            end = codes[i + 1][0] if i + 1 < len(codes) else len(au)
            t = au[e] & 0x1F
            has_param = has_param or t in (7, 8)
            has_idr = has_idr or t == 5
        if has_param and has_idr:
            if self.head_au is None:
                ConsoleLog("[src] head_au（SPS/PPS+IDR）已就绪")
            self.head_au = au
        self.publish(au)


def ConsoleLog(msg):
    print(msg, flush=True)


def adb_shell_bin(device, args, timeout=20):
    r = subprocess.run(["adb", "-s", device, "exec-out"] + args,
                       capture_output=True, timeout=timeout)
    return r.stdout


# ─────────────────────────── 客户端 ───────────────────────────

class Client:
    def __init__(self, sock, addr, broadcaster, injector):
        self.sock = sock
        self.addr = addr
        self.b = broadcaster
        self.injector = injector
        self.q = queue.Queue(maxsize=256)
        self.alive = True
        self.mode = "proto"
        self.send_lock = threading.Lock()
        # 触摸合成状态
        self.touch = None        # {"x0","y0","lx","ly","ix","iy","moved"}

    def send_video(self, payload):
        if not self.alive:
            return
        try:
            self.q.put_nowait(payload)
        except queue.Full:
            # 慢客户端：丢最老的一帧，腾位放新帧（保持"尽量新"）
            try:
                self.q.get_nowait()
                self.q.put_nowait(payload)
            except Exception:
                pass
            self.b.dropped += 1

    def _send(self, ftype, payload=b""):
        if len(payload) > MAX_FRAME:
            raise ValueError("帧超上限")
        with self.send_lock:
            if self.mode == "raw":
                self.sock.sendall(payload)          # 直连解码器：裸流
            else:
                self.sock.sendall(struct.pack("!IB", len(payload), ftype) + payload)

    def hello(self, meta):
        """双模握手：1.5s 内发来 CATCLAW/1 → 协议帧模式；否则 raw 模式（纯 Annex-B/
        纯 MJPEG 字节流，无握手行无帧头——专供 `ffmpeg -i tcp://…` 这类直连解码器，
        文档验收命令因此可以原样跑）。"""
        self.sock.settimeout(1.5)
        raw = b""
        try:
            while b"\n" not in raw:
                chunk = self.sock.recv(64)
                if not chunk:
                    return False
                raw += chunk
                if len(raw) > 64:
                    break
        except socket.timeout:
            raw = b""
        finally:
            self.sock.settimeout(None)
        try:
            self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        except Exception:
            pass
        line = raw.split(b"\n", 1)[0].strip() if raw else b""
        if line == HELLO:
            self.mode = "proto"
            self.sock.sendall(("OK %d %d %s %d\n" % meta).encode("ascii"))
            return True
        if not raw:
            self.mode = "raw"          # 没说话 → 当解码器直连
            log("[cli %s] raw 模式（未握手，纯流输出）" % (self.addr,))
            return True
        try:
            self.sock.sendall(b"ERR expect CATCLAW/1\n")
        except Exception:
            pass
        return False

    def loop(self):
        t = threading.Thread(target=self._writer, daemon=True, name="cli-writer")
        t.start()
        if self.mode == "raw":
            # 只等对端关闭；不发握手行/帧头，输入帧无从谈起（解码器本来也不发）
            try:
                self.sock.settimeout(None)
                while self.alive:
                    if not self.sock.recv(65536):
                        break
            except OSError:
                pass
            self.alive = False
            self.b.remove(self)
            try:
                self.sock.close()
            except Exception:
                pass
            log("[cli %s] raw 断开" % (self.addr,))
            return
        hdr = b""
        try:
            while self.alive:
                while len(hdr) < 5:
                    chunk = self.sock.recv(5 - len(hdr))
                    if not chunk:
                        return
                    hdr += chunk
                length, ftype = struct.unpack("!IB", hdr)
                hdr = b""
                if length > MAX_FRAME:
                    log("[cli %s] 帧超上限 %dB → 断开" % (self.addr, length))
                    return
                payload = b""
                while len(payload) < length:
                    chunk = self.sock.recv(min(65536, length - len(payload)))
                    if not chunk:
                        return
                    payload += chunk
                if ftype == 0x00:
                    log("[cli %s] 对端请求关闭" % (self.addr,))
                    return
                if ftype == 0x02:
                    pass              # 客户端心跳：按协议静默接受（双向心跳）
                elif ftype == 0x10 and length == 5:
                    x, y, action = struct.unpack("!HHB", payload)
                    self._touch(x, y, action)
                elif ftype == 0x11 and length == 5:
                    keycode, action = struct.unpack("!IB", payload)
                    if action == 0:
                        self.injector.submit(["input", "keyevent", str(keycode)])
                elif ftype == 0x12 and length == 4:
                    dx, dy = struct.unpack("!hh", payload)
                    self._scroll(dx, dy)
                else:
                    log("[cli %s] 未知/异常帧 type=0x%02x len=%d（按长度跳过）"
                        % (self.addr, ftype, length))
        except (ConnectionResetError, socket.timeout, OSError):
            pass
        finally:
            self.alive = False
            self.b.remove(self)
            try:
                self.sock.close()
            except Exception:
                pass
            log("[cli %s] 断开（在线 %d）" % (self.addr, len(self.b.clients)))

    def _touch(self, x, y, action):
        if action == 0:      # down：仅记录起点，tap/swipe 在 up 时合成（见 README 限制）
            self.touch = {"x0": x, "y0": y, "lx": x, "ly": y, "moved": False}
        elif action == 2 and self.touch:   # move
            t = self.touch
            if abs(x - t["x0"]) + abs(y - t["y0"]) > 8:
                t["moved"] = True
            if t["moved"] and (abs(x - t["lx"]) + abs(y - t["ly"]) > 6):
                self.injector.submit(["input", "swipe", str(t["lx"]), str(t["ly"]),
                                      str(x), str(y), "60"])
                t["lx"], t["ly"] = x, y
        elif action == 1 and self.touch:   # up
            t = self.touch
            if t["moved"]:
                self.injector.submit(["input", "swipe", str(t["lx"]), str(t["ly"]),
                                      str(x), str(y), "60"])
            else:
                self.injector.submit(["input", "tap", str(x), str(y)])
            self.touch = None

    def _scroll(self, dx, dy):
        meta = self.injector.size or (1280, 688)
        cx, cy = meta[0] // 2, meta[1] // 2
        amp = max(-300, min(300, dy or (dx * 2)))
        # 惯例：滚轮向下(dy>0)=内容下移 → 手指上滑（y 减小）。近似语义，README 已注明。
        self.injector.submit(["input", "swipe", str(cx), str(cy),
                              str(cx), str(cy - amp), "180"])

    def _writer(self):
        last_beat = 0.0
        try:
            while self.alive:
                try:
                    payload = self.q.get(timeout=1.0)
                except queue.Empty:
                    payload = None
                now = time.time()
                if now - last_beat >= 5.0:
                    if self.mode == "proto":       # raw 模式绝不插心跳（会污染裸流）
                        self._send(0x02, struct.pack("!Q", int(now * 1000)))
                    last_beat = now
                if payload is not None:
                    self._send(0x01, payload)
        except Exception:
            self.alive = False


# ─────────────────────────── 主入口 ───────────────────────────

def main():
    ap = argparse.ArgumentParser(description="CATCLAW/1 Waydroid 流服务端")
    ap.add_argument("--port", type=int, default=27183)
    ap.add_argument("--codec", default="auto", choices=["auto", "h264", "jpeg"])
    ap.add_argument("--source", default="auto", choices=["auto", "screenrecord", "jpeg", "scrcpy"])
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--device", default="auto")
    ap.add_argument("--bit-rate", default="8M")
    ap.add_argument("--no-overlay", action="store_true",
                    help="关闭 screenrecord --bugreport 覆盖层（静止画面将无帧流，不建议）")
    args = ap.parse_args()

    dev = pick_device(args.device)
    w, h = wm_size(dev)
    codec = "h264" if args.codec in ("auto", "h264") else "jpeg"
    log("设备=%s 分辨率=%dx%d codec=%s port=%d" % (dev, w, h, codec, args.port))

    injector = input_inj.Injector(dev, (w, h))
    b = Broadcaster()

    if args.source == "screenrecord":
        # 备选源（⚠ 纯 VFR：静止画面零帧，联调/验收请用默认 screencap 路线）
        src = ScreenrecordSource(dev, b, args.bit_rate,
                                 overlay=not args.no_overlay, fps=min(args.fps, 30))
        codec = "h264"
    elif codec == "h264":
        src = ScreencapSource(dev, b, out_codec="h264", fps=min(args.fps, 30))
    else:
        src = ScreencapSource(dev, b, out_codec="jpeg", fps=min(args.fps, 15))
    src.start()

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", args.port))
    srv.listen(8)
    log("监听 0.0.0.0:%d —— ffmpeg -i tcp://<host>:%d 即可验证" % (args.port, args.port))

    def stats():
        while True:
            time.sleep(10)
            line = ("[stat] 帧数=%d fps=%.1f 在线=%d 丢帧=%d 送达段=%d pump=%dB"
                    % (src.frames, src.fps(), len(b.clients), b.dropped, b.sent,
                       getattr(src, "pump_bytes", -1)))
            if hasattr(src, "last_params"):
                line += " last_params=%dB last_au=%s" % (
                    len(src.last_params),
                    "None" if src.last_au is None else "%dB" % len(src.last_au))
            log(line)

    threading.Thread(target=stats, daemon=True).start()

    try:
        while True:
            sock, addr = srv.accept()
            c = Client(sock, addr, b, injector)
            if not c.hello((w, h, codec, args.fps)):
                try:
                    sock.close()
                except Exception:
                    pass
                continue
            b.add(c)
            # 新客户端先补「SPS/PPS+IDR」帧：宿主转码层每秒产出一个 IDR（-g 30），
            # head_au 是最近那个 —— 解码器立即有可解起点，黑屏不超过 1s。
            if codec == "h264" and getattr(src, "head_au", None):
                c.send_video(src.head_au)
            log("[cli %s] 接入（在线 %d，codec=%s %dx%d@%d，mode=%s）"
                % (addr, len(b.clients), codec, w, h, args.fps, c.mode))
            threading.Thread(target=c.loop, daemon=True, name="cli-reader").start()
    except KeyboardInterrupt:
        log("退出")
    finally:
        src.stop()


if __name__ == "__main__":
    main()
