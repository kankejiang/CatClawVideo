@echo off
rem ══════════════════════════════════════════════════════════════
rem  一键投屏：双击 → 看到 108 的 waydroid 安卓画面
rem  · 服务端未运行 → 自动 ssh 拉起（需 root@10.0.0.108 免密 ssh）
rem  · 断线自动重连；窗口内 Esc 或点 × 退出（正常退出不重连）
rem ══════════════════════════════════════════════════════════════
setlocal
set HOST=10.0.0.108
set PORT=27283
set DIR=%~dp0
set EXE=%DIR%bin\Debug\net11.0-windows\stream-probe.exe

rem ① 首次运行先编译（已编译则跳过，秒开）
if not exist "%EXE%" (
    echo [launch] 首次运行，编译中...
    dotnet build "%DIR%stream-probe.csproj" -v q -nologo
)

rem ② 连接观看；连接失败（服务端没起/断线）→ ssh 拉起服务端 → 重试
:retry
"%EXE%" --connect %HOST%:%PORT%
if errorlevel 1 (
    echo [launch] 连接失败，ssh 拉起 %HOST%:/root/waydroid-stream ...
    ssh root@%HOST% "cd /root/waydroid-stream && bash serve.sh -d --port %PORT% --codec h264 --fps 30"
    if errorlevel 1 (
        echo [launch] ssh 拉起失败：请确认 1^) ssh root@%HOST% 免密可登录 2^) 108 上有 /root/waydroid-stream/serve.sh
        pause
        exit /b 1
    )
    echo [launch] 等待采集源预热 12 秒...
    timeout /t 12 /nobreak >nul
    goto retry
)
endlocal
