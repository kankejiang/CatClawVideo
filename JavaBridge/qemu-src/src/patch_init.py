"""把 harness3.c 补成"能当 initramfs 里的 /init 用"的形态：
   ① main 里先挂 /proc（引擎会读 /proc/self/*）
   ② 无条件跑完整调用链（不再依赖命令行开关）
"""
import io
import re

P = r'D:\Code\_scratch_tb\thunder-harness\harness3.c'
src = io.open(P, encoding='utf-8').read()

# ① 补头文件
if '#include <sys/mount.h>' not in src:
    src = src.replace('#include <unistd.h>',
                      '#include <unistd.h>\n#include <sys/mount.h>\n#include <sys/stat.h>\n#include <errno.h>')

# ② main 里挂载 /proc 与 /dev，并跑完整链
old_tail = re.search(r'(    printf\("\[v2\] ===== 结束，JNI 总调用 %d 次 =====\\n", g_jni_calls\);\n    return 0;\n\}\n)', src)
mount_block = '''    // ── 当 initramfs 的 /init 用时：先把伪文件系统挂起来 ──
    // 引擎会读 /proc（自检、线程信息等），不挂会出现奇怪的行为差异。
    mkdir("/proc", 0555);
    mkdir("/dev", 0755);
    mkdir("/tmp", 0777);
    mkdir("/thunder-data", 0777);
    if (mount("proc", "/proc", "proc", 0, NULL) != 0)
        printf("[init] mount /proc 失败 errno=%d（不致命）\\n", errno);
    else
        printf("[init] /proc 已挂载\\n");
    mount("devtmpfs", "/dev", "devtmpfs", 0, NULL);   // 失败不致命

    printf("[init] ===== 开始完整调用链 =====\\n");
    run_full_chain(env, sdk);
    fflush(stdout);

    printf("[v2] ===== 结束，JNI 总调用 %d 次 =====\\n", g_jni_calls);
    // 容器/VM 里作为 init：不要退出（退出会 kernel panic）。等一会儿再重启 shell。
    for (;;) sleep(3600);
    return 0;
}
'''
src = src[:old_tail.start()] + mount_block + src[old_tail.end():]

io.open(P, 'w', encoding='utf-8', newline='\n').write(src)
print('harness3.c 已补 init 逻辑，行数', src.count('\n'))
