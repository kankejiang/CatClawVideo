#!/usr/bin/env python3
# 批量试非 Guard 源：找 ext 为空即可出分类的源，然后全链路计时
import socket, json, time

s = socket.create_connection(("127.0.0.1", 18600), timeout=15)
f = s.makefile("rwb")

def rpc(obj, timeout=300):
    f.write((json.dumps(obj) + "\n").encode()); f.flush()
    t0 = time.time()
    while True:
        line = f.readline()
        if not line: raise RuntimeError("EOF")
        resp = json.loads(line)
        if resp.get("id") == obj["id"]:
            return resp, time.time() - t0

CAND = ["Auete", "Libvio", "Dm84", "Jpys", "Kekys", "Ddrk", "Bttwoo",
        "AppYsV2", "AppSK", "AppMao", "AppTT", "NanGua", "Alllive", "Anime1"]
picked = None
for i, name in enumerate(CAND, start=10):
    try:
        resp, t = rpc({"id":i, "op":"load", "site":"b%d" % i, "className":name,
                       "ext":"", "jars":["/fty.jar"]}, timeout=60)
        if not resp.get("ok"):
            print("%-10s load 失败 %s" % (name, str(resp.get("error",""))[:60])); continue
        resp, t = rpc({"id":i, "op":"call", "site":"b%d" % i, "method":"homeContent",
                       "args":[""]}, timeout=60)
        if not resp.get("ok"):
            print("%-10s home 失败 %s" % (name, str(resp.get("error",""))[:60])); continue
        r = resp.get("result")
        r = json.loads(r) if isinstance(r, str) else (r or {})
        cls = r.get("class") or []
        if cls:
            print("★ %-10s home %.3fs 分类数=%d" % (name, t, len(cls)))
            picked = ("b%d" % i, name, cls[0].get("type_id"), i)
            break
        print("%-10s home 空分类" % name)
    except RuntimeError as e:
        print("%-10s 桥死: %s" % (name, e)); break
    except Exception as e:
        print("%-10s 异常 %s" % (name, str(e)[:60]))

if picked:
    site, name, tid, i = picked
    def safe_json(v):
        try:
            return json.loads(v) if isinstance(v, str) else (v or {})
        except Exception:
            return {}
    resp, t = rpc({"id":i+100, "op":"call", "site":site, "method":"categoryContent",
                   "args":[str(tid), "1", "false", ""]}, timeout=120)
    movies = (safe_json(resp.get("result")).get("list") or [])
    print("categoryContent(%s): %.3fs ok=%s 影片数=%d" % (tid, t, resp.get("ok"), len(movies)))
    if movies:
        vid = movies[0].get("vod_id")
        resp, t = rpc({"id":i+101, "op":"call", "site":site, "method":"detailContent",
                       "args":[str(vid)]}, timeout=120)
        head = str(resp.get("result",""))[:180].replace("\n", " ")
        print("detailContent(%s): %.3fs ok=%s（aarch64 TCG 对照 71~90s）| %s" % (vid, t, resp.get("ok"), head))
    if movies:
        vid = movies[0].get("vod_id")
        resp, t = rpc({"id":i+101, "op":"call", "site":site, "method":"detailContent",
                       "args":[str(vid)]}, timeout=120)
        print("detailContent(%s): %.3fs ok=%s（aarch64 TCG 对照 71~90s）" % (vid, t, resp.get("ok")))
