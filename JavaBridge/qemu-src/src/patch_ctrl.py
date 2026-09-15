# -*- coding: utf-8 -*-
"""把控制通道接进 harness4.c：include ctrlloop.c、注册引擎函数、main 进事件循环。"""
import io

p = r'D:\Code\_scratch_tb\thunder-harness\harness4.c'
s = io.open(p, encoding='utf-8').read()

def rep(old, new, tag):
    global s
    if old not in s:
        print('!! 锚点未找到:', tag); return False
    s = s.replace(old, new, 1); print('   ok', tag); return True

# 1) include（放在 main 之前，此时 JObj/代理/全局都已定义）
rep('int main(void) {',
    '// ↓↓↓ 控制通道（宿主 App ⇄ guest）：用 qemu 用户网络的 10.0.2.2 回连宿主 ↓↓↓\n'
    '#include "ctrlloop.c"\n\n'
    'int main(void) {', 'include ctrlloop.c')

# 2) 在调用链里注册引擎函数指针
rep('''    if (unInit && getenv("CALL_UNINIT") && atoi(getenv("CALL_UNINIT"))) unInit(env, (jobject)thiz);''',
'''    if (unInit && getenv("CALL_UNINIT") && atoi(getenv("CALL_UNINIT"))) unInit(env, (jobject)thiz);
    // 把引擎函数指针交给控制通道（运行时下发任务用）
    {
        EngineFns e; memset(&e, 0, sizeof e);
        e.env = (void *)env;  e.thiz = (void *)thiz;
        e.createMagnet = (void *)createMagnet;
        e.createP2sp   = (void *)createP2spTask;
        e.startTask    = (void *)startTask;
        e.gsState      = (void *)gsState;
        e.getTaskInfo  = (void *)getTaskInfo;
        e.localUrl     = (void *)localUrl;
        ctrl_register_engine(&e);
    }''', '注册引擎函数')

# 3) main 尾部：起代理（可选）+ 进事件循环
old_tail = '''    if (g_engine_port > 0 && getenv("PROXY_PORT")) {
        int lp = atoi(getenv("PROXY_PORT"));
        // 用 pthread 跑 accept 循环（fork 会卡、顺序版会挡住自测）
        pthread_t th;
        if (pthread_create(&th, NULL, proxy_entry, (void *)(intptr_t)lp) == 0) {
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
        } else {
            printf("[proxy] pthread 起不来，退回顺序版\\n");
            proxy_loop(lp, g_engine_port);
        }
    }
    for (;;) sleep(3600);'''
new_tail = '''    if (getenv("CTRL_PORT")) g_ctrl_port = atoi(getenv("CTRL_PORT"));
    if (g_engine_port > 0 && getenv("PROXY_PORT")) {
        int lp = atoi(getenv("PROXY_PORT"));
        pthread_t th;
        if (pthread_create(&th, NULL, proxy_entry, (void *)(intptr_t)lp) == 0) {
            sleep(1);
            printf("[proxy] 已启动（端口 %d → 引擎 %d）\\n", lp, g_engine_port);
        } else {
            printf("[proxy] pthread 起不来\\n");
        }
    }
    // 主事件循环：控制通道轮询 + 代理的"重新武装"握手（引擎调用必须在本线程）
    printf("[main] 进入事件循环（控制端口 %d）\\n", g_ctrl_port);
    fflush(stdout);
    main_loop();
    for (;;) sleep(3600);'''
rep(old_tail, new_tail, 'main 事件循环')

io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('完成; ctrlloop 引用 =', s.count('ctrlloop.c'), 'main_loop =', s.count('main_loop'))
