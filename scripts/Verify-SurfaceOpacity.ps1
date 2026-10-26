[CmdletBinding()]
param([string]$PlayerPath, [string]$VideoPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (!$PlayerPath) { $PlayerPath = Join-Path $taskRoot 'NovaPlayer.App/bin/Release/net10.0-windows/NovaPlayer.exe' }
if (!$VideoPath) { $VideoPath = Join-Path $taskRoot 'artifacts/真实视频 核心验证.mp4' }
$exe = (Resolve-Path -LiteralPath $PlayerPath).Path
$video = (Resolve-Path -LiteralPath $VideoPath).Path
$folder = Join-Path $taskRoot ('artifacts/opacity-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $folder | Out-Null
$report = Join-Path $folder 'opacity.json'
$process = Start-Process -FilePath $exe -ArgumentList '--verify-opacity', ('"' + $video + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
try {
    if (!$process.WaitForExit(90000)) { $process.Kill(); throw '不透明表面验证超时。' }
    $process.WaitForExit()
    if (!(Test-Path -LiteralPath $report)) { throw "没有生成表面验证报告，退出码：$($process.ExitCode)" }
    $result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Output "表面验证：$report"
    if ($process.ExitCode -ne 0 -or !$result.passed) { Write-Output ($result | ConvertTo-Json -Depth 6); throw '不透明表面验证失败。' }
    Write-Output "通过 $($result.checks.Count) 项不透明表面检查。"
} finally { $process.Dispose() }
