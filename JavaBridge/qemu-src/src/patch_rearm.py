# -*- coding: utf-8 -*-
"""把代理的“重新武装”改成主线程握手（引擎状态线程相关，代理线程里调会得到 9102）。"""
import io

p = r'D:\Code\_scratch_tb\thunder-harness\harness4.c'
s = io.open(p, encoding='utf-8').read()

def rep(old, new, tag):
    global s
    if old not in s:
        print('!! 锚点未找到:', tag); return False
    s = s.replace(old, new, 1)
    print('   ok', tag); return True

rep('static void *g_localurl_fn = NULL;    // Java_..._getLocalUrl 的函数指针',
'''static void *g_localurl_fn = NULL;    // Java_..._getLocalUrl 的函数指针
// 引擎状态是**线程相关**的：在代理线程里调 getLocalUrl 会返回 9102(XL_SDK_NOT_INIT)。
// 所以由代理线程发起请求、**主线程**执行 getLocalUrl，用一组 volatile 变量握手。
static volatile int g_rearm_req = 0, g_rearm_done = 0, g_rearm_port = 0;''', '握手变量')

rep('static int rearm_engine(void) {', 'static int do_rearm(void) {', 'rearm→do_rearm')

rep('''    int np = rearm_engine();            // ★ 每次连接都重新武装
    if (np > 0) tport = np;''',
'''    // ★ 请主线程重新武装（引擎状态线程相关），并等它回结果
    g_rearm_port = 0; g_rearm_done = 0; g_rearm_req = 1;
    for (int i = 0; i < 200 && !g_rearm_done; i++) usleep(50000);   // 最多等 10s
    if (g_rearm_port > 0) tport = g_rearm_port;''', '代理线程改为请求主线程')

rep('''        if (pthread_create(&th, NULL, proxy_entry, (void *)(intptr_t)lp) == 0) {
            sleep(2);                                  // 等监听就绪
            char u[300];
            snprintf(u, sizeof u, "http://127.0.0.1:%d%s", lp, g_engine_path);
            printf("[proxy] === guest 内「经代理」自测 ===\\n");
            http_probe(u);                             // ★ 经代理端口再拉一次
            fflush(stdout);
        } else {''',
'''        if (pthread_create(&th, NULL, proxy_entry, (void *)(intptr_t)lp) == 0) {
            sleep(1);
            printf("[proxy] 主线程握手循环已启动\\n");
            fflush(stdout);
            for (;;) {
                if (g_rearm_req) {
                    g_rearm_req = 0;
                    g_rearm_port = do_rearm();          // ★ 在主线程里调，才不会是 9102
                    g_rearm_done = 1;
                }
                usleep(20000);
            }
        } else {''', 'main 握手循环')

io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('完成; g_rearm_req 次数 =', s.count('g_rearm_req'))
