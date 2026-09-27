#!/usr/bin/env python3
"""验控：假控制端（127.0.0.1:18080）——验证 harness 的 /task 轮询与 /report 上报闭环。
用法：python3 thunder_probe.py [首轮task体(默认PING)] [运行秒数(默认45)]
harness 侧：CTRL_HOST=127.0.0.1 CTRL_PORT=18080 ... ./harness
"""
import http.server, threading, sys, time

task_body = sys.argv[1] if len(sys.argv) > 1 else "PING"
run_secs = int(sys.argv[2]) if len(sys.argv) > 2 else 45
hits = {"task": 0, "report": 0}

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/task"):
            hits["task"] += 1
            body = (task_body if hits["task"] <= 2 else "NONE") + "\n"  # 前两轮发任务体
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body.encode())
        elif self.path.startswith("/report"):
            hits["report"] += 1
            print("[报告 %d] %s" % (hits["report"], self.path[:220]), flush=True)
            self.send_response(200)
            self.send_header("Content-Length", "2")
            self.end_headers()
            self.wfile.write(b"ok")
        else:
            print("[其它] %s" % self.path[:160], flush=True)
            self.send_response(404)
            self.end_headers()
    def log_message(self, *a):
        pass

srv = http.server.HTTPServer(("127.0.0.1", 18080), H)
threading.Thread(target=srv.serve_forever, daemon=True).start()
print("[验控] 监听 127.0.0.1:18080，首轮 /task → %s" % task_body, flush=True)
time.sleep(run_secs)
print("[验控] 汇总：task 次数=%d report 次数=%d" % (hits["task"], hits["report"]), flush=True)
