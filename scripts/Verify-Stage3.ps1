[CmdletBinding()]
param([string]$VideoPath, [string]$PlayerPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
if (!$VideoPath) { $VideoPath = Join-Path $taskRoot 'artifacts/真实视频 核心验证.mp4' }
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
$runFolder = Join-Path $taskRoot "artifacts/stage3-$([guid]::NewGuid().ToString('N'))"
$fixtures = Join-Path $runFolder 'fixtures'
New-Item -ItemType Directory -Force $fixtures | Out-Null
foreach ($name in @('a 中文视频.mp4', 'b 第二段.mp4', 'c 缺失视频.mp4')) {
    Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $fixtures $name)
}
$file = Join-Path $fixtures 'a 中文视频.mp4'
$exe = if ($PlayerPath) { (Resolve-Path -LiteralPath $PlayerPath).Path } else { Join-Path $taskRoot 'NovaPlayer.App/bin/Release/net10.0-windows/NovaPlayer.App.exe' }
$total = 0
foreach ($phase in @('seed', 'resume', 'flow')) {
    $report = Join-Path $runFolder "$phase.json"
    $process = Start-Process -FilePath $exe -ArgumentList "--verify-library-$phase", ('"' + $file + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(90000)) { $process.Kill(); throw "第三阶段 $phase 验收超时。" }
    $process.WaitForExit()
    if (!(Test-Path -LiteralPath $report)) { throw "未生成验收报告：$report，退出码：$($process.ExitCode)" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $count = @($result.checks | Where-Object passed).Count
    $total += $count
    Write-Output "$phase ：$count / $($result.checks.Count)，报告：$report"
    if ($process.ExitCode -ne 0 -or !$result.passed) { Write-Output ($result | ConvertTo-Json -Depth 8); throw '第三阶段验收失败。' }
}
Write-Output "跨进程验收共通过 $total 项，档案和界面导出：$runFolder"
