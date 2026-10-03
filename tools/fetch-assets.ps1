<#
.SYNOPSIS
  取件：主程序需要的两个大二进制（不入 git）。

.DESCRIPTION
  1) mpv-2.dll          ← kankejiang/CatClawVideo 的 assets-v1（本仓库 tools/assets.json 校验）
                         解到 CatClawVideo.Maui\Platforms\Windows\libs\
  2) QEMU guest 运行时  ← 兄弟仓库 CatClaw.Qemu 的 vm-assets-v1（转调它的 fetch-assets.ps1）
                         解到 CatClawVideo.Maui\QemuGuest\

  缓存目录 %LOCALAPPDATA%\CatClawQemuAssets：命中且 SHA256 一致就直接用，不再下载。
  网络不可用时脚本会以非零码退出，但不会留下半个文件（校验失败即删）。

.EXAMPLE
  pwsh tools/fetch-assets.ps1                 # 补齐两个大件
  pwsh tools/fetch-assets.ps1 -SkipVm         # 只要 mpv-2.dll
#>
[CmdletBinding()]
param(
    [string]$Dest = (Split-Path -Parent $PSScriptRoot),
    [string]$Cache = (Join-Path $env:LOCALAPPDATA "CatClawQemuAssets"),
    [switch]$SkipVm,
    [switch]$SkipMpv,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

function Get-Asset($name, [int64]$size, [string]$sha256, [string]$repo, [string]$tag) {
    $cached = Join-Path $Cache $name
    if ((-not $Force) -and (Test-Path $cached)) {
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $cached).Hash.ToLower() -eq $sha256) {
            Write-Host "缓存命中：$name"
            return $cached
        }
        Remove-Item -LiteralPath $cached -Force
    }
    $url = "https://github.com/$repo/releases/download/$tag/$name"
    Write-Host ("下载 {0}（{1:N1} MB）← {2}" -f $name, ($size / 1MB), $url)
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) { & $curl.Source -L --fail --retry 3 -o $cached $url }
    else { Invoke-WebRequest -Uri $url -OutFile $cached -UseBasicParsing }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $cached).Hash.ToLower()
    if ($actual -ne $sha256) {
        Remove-Item -LiteralPath $cached -Force
        throw "SHA256 不匹配：$name（期望 $sha256，实际 $actual）"
    }
    return $cached
}

# ① mpv-2.dll（本仓库 Release）
if (-not $SkipMpv) {
    $manifestPath = Join-Path $PSScriptRoot "assets.json"
    if (Test-Path $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        foreach ($a in $manifest.assets) {
            $targetDir = Join-Path $Dest $a.dest
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
            $target = Join-Path $targetDir $a.name
            if ((Test-Path $target) -and (-not $Force) -and
                ((Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLower() -eq $a.sha256)) {
                Write-Host "已在位：$($a.name)"
                continue
            }
            $file = Get-Asset $a.name $a.size $a.sha256 $manifest.repo $a.tag
            Copy-Item -LiteralPath $file -Destination $target -Force
            Write-Host "已放置：$target"
        }
    }
}

# ② QEMU guest 运行时（兄弟仓库套件：用它的取件脚本，保持清单单一来源）
if (-not $SkipVm) {
    $vmScript = Join-Path (Split-Path -Parent $Dest) "CatClaw.Qemu\tools\fetch-assets.ps1"
    if (Test-Path $vmScript) {
        & $vmScript -Dest (Join-Path $Dest "CatClawVideo.Maui") -Cache $Cache $(if ($Force) { "-Force" })
    } else {
        Write-Host ""
        Write-Host "⚠ 未找到兄弟仓库套件脚本：$vmScript" -ForegroundColor Yellow
        Write-Host "  QEMU guest 运行时未取件（磁力/Guard 源会判引擎未就绪、自动回落内置 BT）。" -ForegroundColor Yellow
        Write-Host "  克隆 https://github.com/kankejiang/CatClaw.Qemu 到同级目录，或手工把 Release 附件解到 CatClawVideo.Maui\QemuGuest\ 。" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "取件完成。"
