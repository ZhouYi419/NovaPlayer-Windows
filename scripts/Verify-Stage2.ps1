[CmdletBinding()]
param([string]$VideoPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$artifacts = Join-Path $taskRoot 'artifacts'
if (!$VideoPath) { $VideoPath = Join-Path $artifacts '真实视频 核心验证.mp4' }
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
$runFolder = Join-Path $artifacts "stage2-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $runFolder | Out-Null
$report = Join-Path $runFolder 'report.json'
$exe = Join-Path $taskRoot 'NovaPlayer.App/bin/Release/net10.0-windows/NovaPlayer.App.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--verify-shell', ('"' + $VideoPath + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { $process.Kill(); throw '主界面验收超时，请查看日志。' }
$process.WaitForExit()
if (!(Test-Path -LiteralPath $report)) { throw "未生成验收报告，退出码：$($process.ExitCode)" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
Write-Output "验收报告：$report"
Write-Output "通过检查：$(($result.checks | Where-Object passed).Count) / $($result.checks.Count)"
Write-Output "界面导出：$runFolder"
if ($process.ExitCode -ne 0 -or !$result.passed) { Write-Output $result.failure; throw '主界面验收失败。' }
