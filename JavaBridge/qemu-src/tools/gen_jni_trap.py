#!/usr/bin/env python3
"""从 NDK 的 jni.h 解析 struct JNINativeInterface 的字段顺序，生成 jni_trap.h。

jni_trap.h 提供：
  - JNI_SLOT_COUNT  = 结构体指针槽数（bionic = 233）
  - JNI_TRAPS[]     = 每槽一个陷阱函数指针（被调用时打印槽位名后返回 0）
  - g_trap_names[]  = 槽位名（诊断用）

背景：仓库丢了原版 jni_trap.h（旧 scratch 目录随 D: 盘消失）。陷阱表的唯一硬约束是
「槽数与 jni.h 的 JNINativeInterface 完全一致」，与其手抄 233 个名字，不如从 NDK 头文件
机器解析——顺序、数量、名字三者天然对齐。

用法：python3 gen_jni_trap.py <ndk-sysroot>/usr/include/jni.h > jni_trap.h
"""
import re
import sys

def main():
    if len(sys.argv) != 2:
        print("usage: gen_jni_trap.py <jni.h>", file=sys.stderr)
        return 1
    src = open(sys.argv[1], encoding="utf-8", errors="replace").read()

    # 取 struct JNINativeInterface { ... }; 的函数指针字段名（按出现顺序）。
    # bionic jni.h 里字段形如：
    #   void*        reserved0;          ← 4 个保留槽
    #   jclass       (*FindClass)(JNIEnv*, const char*);
    m = re.search(r"struct\s+JNINativeInterface\s*\{(.*?)\n\};", src, re.S)
    if not m:
        print("ERROR: struct JNINativeInterface not found", file=sys.stderr)
        return 1
    body = m.group(1)
    names = re.findall(r"\(\s*\*\s*(\w+)\s*\)\s*\(", body)
    reserved = re.findall(r"\bvoid\s*\*\s*(reserved\d)\s*;", body)
    # reserved 槽出现在结构体最前 4 个，按源码顺序合并（reserved 都在开头，稳妥起见按位置排序）
    fields = []
    for mm in re.finditer(r"(\(\s*\*\s*(\w+)\s*\)\s*\()|(void\s*\*\s*(reserved\d)\s*;)", body):
        if mm.group(2):
            fields.append((mm.start(), mm.group(2)))
        elif mm.group(4):
            fields.append((mm.start(), mm.group(4)))
    fields.sort()
    names = [n for _, n in fields]

    count = len(names)
    print(f"// ⚠ 本文件由 tools/gen_jni_trap.py 从 NDK jni.h 自动生成（{count} 槽），勿手改。")
    print(f"// 陷阱：引擎调到未实现的 JNI 槽位时打印槽位名并返回 0（避免裸 NULL 跳转崩溃）。")
    print("#include <stdio.h>")
    print()
    print(f"#define JNI_SLOT_COUNT {count}")
    print()
    print("static const char *g_trap_names[JNI_SLOT_COUNT] = {")
    for i, n in enumerate(names):
        print(f'    "{n}",')
    print("};")
    print()
    print("/* 各槽签名不同，陷阱统一用 4 指针参数签名 + 强转塞入函数表：")
    print(" * 只要不被正确调用就没问题；真被调到时只打印名字（参数值不可信）就返回 0。*/")
    for i, n in enumerate(names):
        print(f'static void *trap_{i}(void *a, void *b, void *c, void *d) {{ (void)a;(void)b;(void)c;(void)d; printf("[trap] JNI.%s(#%d) 被调用\\n", g_trap_names[{i}], {i}); return 0; }}')
    print()
    print("static void *JNI_TRAPS[JNI_SLOT_COUNT] = {")
    for i in range(count):
        print(f"    trap_{i},")
    print("};")
    return 0

if __name__ == "__main__":
    sys.exit(main())
