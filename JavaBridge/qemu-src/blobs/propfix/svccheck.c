// svccheck.c —— B1.1：查 AIDL/HIDL 服务是否在 servicemanager 里注册（排障利器）
//
// 背景：screencap 会等 AIDL 服务名 `SurfaceFlingerAIDL`（libgui.so 里的字符串实证），
// 而 SF 的日志里看不到它的注册行 ⇒ 需要直接问 servicemanager。
//
// 编（108 上 NDK）：
//   x86_64-linux-android33-clang -O2 -o svccheck svccheck.c -lbinder_ndk -llog
#define _GNU_SOURCE
#include <android/binder_ibinder.h>
#include <android/binder_manager.h>   // AServiceManager_getService 的正规声明（含 extern "C"）
#include <android/binder_status.h>
#include <stdio.h>
#include <string.h>


int main(int argc, char **argv) {
    const char *names[] = {
        "SurfaceFlingerAIDL",
        "SurfaceFlinger",
        "android.hardware.graphics.allocator@4.0::IAllocator/default",
        "android.hardware.graphics.composer@2.1::IComposer/default",
        "vendor.waydroid.task@1.0::IWaydroidTask/default",
        "vendor.waydroid.display@1.2::IWaydroidDisplay/default",
        "android.hardware.configstore@1.1::ISurfaceFlingerConfigs/default",
    };
    int n = (int)(sizeof(names) / sizeof(names[0]));
    printf("[svccheck] 查询 %d 个服务：\n", n);
    for (int i = 0; i < n; i++) {
        AIBinder *b = NULL;
        if (argc > 1 && strcmp(argv[1], "-v") == 0) {
            printf("  declared(%s) = %d\n", names[i], (int)AServiceManager_isDeclared(names[i]));
        }
        b = AServiceManager_getService(names[i]);
        printf("  %-70s -> %s\n", names[i], b ? "存在 ✔" : "**不存在** ✗");
        if (b) AIBinder_decStrong(b);
    }
    printf("[svccheck] 结束\n");
    return 0;
}
