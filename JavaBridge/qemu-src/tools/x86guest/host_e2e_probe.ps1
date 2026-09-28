# 宿主端到端取证：时间线 + 引擎日志 + guest console + 进程状态
$data = "$env:APPDATA\CatClawVideo.debug"
$log = "$data\home-debug.log"

Write-Output '=== ① 本次运行时间线（04:31 起，关键行）==='
Select-String -Path $log -Pattern '04:3[1-9]:' -Encoding utf8 |
  Where-Object { $_.Line -match 'QEMU|qemu|ART guest|桥|预热|合并|引擎|重置|QemuGuest|VM' } |
  Select-Object -Last 30 | ForEach-Object { $_.Line }

Write-Output ''
Write-Output '=== ② 迅雷引擎日志尾部（bt.log）==='
Get-Content "$data\logs\bt.log" -Tail 25 -Encoding utf8 | Select-String -Pattern '控制端|外部 VM|引擎|会话|qemu|VM|错误|失败' | Select-Object -Last 15 | ForEach-Object { $_.Line }

Write-Output ''
Write-Output '=== ③ guest console 日志位置 ==='
Get-ChildItem $data -Recurse -Filter '*console*' -ErrorAction SilentlyContinue | Select-Object -First 5 FullName, Length, LastWriteTime | Format-Table -AutoSize

Write-Output '=== ④ 进程 ==='
Get-Process qemu-system-aarch64, CatClawVideo.Maui -ErrorAction SilentlyContinue |
  Select-Object Id, ProcessName, StartTime | Format-Table -AutoSize
