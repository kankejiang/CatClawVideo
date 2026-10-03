@echo off
rem One-stop: bridge.jar -> gb.dex -> bootimg image -> inject current dex -> self-check -> deploy
rem
rem STEP ORDER IS MANDATORY (each step maps to a real incident, 2026-09-30):
rem   1) build_bootimg_inject.py - boot components / libart(verifier+nterp) patches / init's
rem      -Ximage line and the THUNDER SECTION. Only this script can produce init (it rebuilds
rem      from pristine; pristine has no thunder section).
rem   2) inject_dex.py - swap in the CURRENT gb.dex / ui_stub.dex. The bootimg pipeline INHERITS
rem      the OLD dexes from pristine, so skipping this = image builds fine but magnet silently
rem      dies and the verifier/netdisk fixes are lost.
rem   3) check_image.py - do NOT deploy when it fails. All of the above degrade silently at
rem      packaging time (only visible at runtime); the T5 bench M1 failure is how it was found.
rem
rem NOTE: keep every comment ASCII-only. cmd.exe decodes this file with the OEM codepage, so
rem multibyte (Chinese/emoji) comment text gets mangled into stray tokens and reported as
rem "'xxx' is not recognized as an internal or external command" (hit on 2026-10-01).
rem Also: the run-position copy FAILS while the app is running (the image is user-mapped by
rem QEMU) -> close the app before running this script.
setlocal
set JB=d:\Code\CatClawVideo\JavaBridge
set Z=d:\Code\CatClawVideo\CatClawVideo.Maui\QemuGuest\x86guest
set SRC=%Z%\art_initrd_x64.gz
set BOOT=%Z%\art_initrd_x64_bootimg.gz
set TMP=d:\Code\CatClawVideo\.zwork\art_initrd_x64_final.gz
set RUN=d:\Code\CatClawVideo\CatClawVideo.Maui\bin\Debug\net11.0-windows10.0.26100.0\win-x64\QemuGuest\x86guest

cd /d %JB% || exit /b 1
call build.cmd || exit /b 2
python %JB%\qemu-src\tools\build_gb_dex.py || exit /b 3
python %JB%\qemu-src\tools\x86guest\build_bootimg_inject.py || exit /b 4
rem B1.0: also inject adbd (x86-64 dynamic) + its libs, so Server.startAdbd can launch it
rem and the host can `adb connect 127.0.0.1:<adbPort>`. "+0755:<path>=<file>" = ADD a new entry.
python %JB%\qemu-src\tools\x86guest\inject_initrd.py "%BOOT%" "%TMP%" "gb.dex=%JB%\qemu-src\art\gb.dex" "ui_stub.dex=%JB%\qemu-src\art\ui_stub.dex" "+0755:system/bin/adbd=%JB%\qemu-src\blobs\adbd\adbd" "system/lib64/libadbd_auth.so=%JB%\qemu-src\blobs\adbd\libadbd_auth.so" "system/lib64/libadbd_fs.so=%JB%\qemu-src\blobs\adbd\libadbd_fs.so" "+0644:b1/weston.tar.gz=%JB%\qemu-src\blobs\weston\weston.tar.gz" "+0644:b1/mods.tar.gz=%JB%\qemu-src\blobs\mods\mods.tar.gz" "+0644:b1/android-stack.tar.gz=%JB%\qemu-src\blobs\astack\android-stack.tar.gz" "+0644:system/lib64/libpropfix.so=%JB%\qemu-src\blobs\propfix\libpropfix.so" "+0755:system/bin/propinit=%JB%\qemu-src\blobs\propfix\propinit" "+0755:system/bin/mkbinders=%JB%\qemu-src\blobs\propfix\mkbinders" "+0755:system/bin/eglprobe=%JB%\qemu-src\blobs\propfix\eglprobe" "+0755:system/bin/gbmprobe=%JB%\qemu-src\blobs\propfix\gbmprobe" "+0755:system/bin/svccheck=%JB%\qemu-src\blobs\propfix\svccheck" "+0644:system/lib64/libLLVM22.so=%JB%\qemu-src\blobs\propfix\libLLVM22.so" "+0644:b1/mesa-gl.tar.gz=%JB%\qemu-src\blobs\mesa\mesa-gl.tar.gz" "+0644:vendor/lib64/virtio_gpu_gbm.so=%JB%\qemu-src\blobs\mesa\virtio_gpu_gbm.so" || exit /b 5
python %JB%\qemu-src\tools\x86guest\check_image.py "%TMP%" || exit /b 6
copy /y "%TMP%" "%SRC%.new" || exit /b 7
move /y "%SRC%.new" "%SRC%" || exit /b 8
copy /y "%SRC%" "%RUN%\art_initrd_x64.gz.new" || exit /b 9
move /y "%RUN%\art_initrd_x64.gz.new" "%RUN%\art_initrd_x64.gz" || exit /b 10
echo DEPLOYED.
