[CmdletBinding()]
param([string]$PlayerPath, [string]$PlanPath, [ValidateRange(10,86400)][int]$SoakSeconds = 120)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$exe = if ($PlayerPath) { (Resolve-Path -LiteralPath $PlayerPath).Path } else { Join-Path $taskRoot 'artifacts/player/NovaPlayer.exe' }
$runFolder = Join-Path $taskRoot "artifacts/stage5-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $runFolder | Out-Null
if (!$PlanPath) { $PlanPath = & "$PSScriptRoot/New-ReleaseFixtures.ps1" -OutputDirectory (Join-Path $runFolder 'fixtures') -SoakSeconds $SoakSeconds }
$PlanPath = (Resolve-Path -LiteralPath $PlanPath).Path
$report = Join-Path $runFolder 'release.json'
$process = Start-Process -FilePath $exe -ArgumentList '--verify-release', ('"' + $PlanPath + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds($SoakSeconds + 600)
while (!$process.WaitForExit(1000)) {
    if ([DateTime]::UtcNow -gt $deadline) { $process.Kill(); throw '发布兼容性与稳定性验收超时。' }
}
$process.WaitForExit()
if (!(Test-Path -LiteralPath $report)) { throw "未生成报告：$report，退出码：$($process.ExitCode)" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
Write-Output "发布验收：$(@($result.checks | Where-Object passed).Count) / $($result.checks.Count)，报告：$report"
if ($process.ExitCode -ne 0 -or !$result.passed) { Write-Output ($result | ConvertTo-Json -Depth 10); throw '发布兼容性或稳定性验收失败。' }
