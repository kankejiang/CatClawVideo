/* binfmt 端到端自测：静态 arm64，-nostdlib 裸 syscall
 * 经 binfmt_misc(arm64_exe) → ndk_translation runner 转译执行
 * 成功标志：stdout 打出 BINFMT_SELFTEST_OK
 */
static long sys3(long n, long a, long b, long c) {
    register long x8 __asm__("x8") = n;
    register long x0 __asm__("x0") = a;
    register long x1 __asm__("x1") = b;
    register long x2 __asm__("x2") = c;
    __asm__ volatile("svc 0" : "+r"(x0) : "r"(x8), "r"(x1), "r"(x2) : "memory");
    return x0;
}

void _start(void) {
    const char msg[] = "BINFMT_SELFTEST_OK arm64 via binfmt_misc\n";
    sys3(64, 1, (long)msg, sizeof(msg) - 1); /* write(1, msg, len) */
    sys3(93, 0, 0, 0);                        /* exit(0) */
    for (;;) {}
}
