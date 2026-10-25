[CmdletBinding()]
param([string]$VideoPath, [ValidateRange(1, 10)][int]$Runs = 3, [string]$PlayerPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$artifacts = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
if (!$VideoPath) {
    $ffmpeg = Join-Path $taskRoot '.tools/ffmpeg/ffmpeg.exe'
    if (!(Test-Path -LiteralPath $ffmpeg)) { throw 'Run Setup-Dependencies.ps1 -IncludeVerificationTools first, or provide -VideoPath (at least 45 seconds).' }
    $VideoPath = Join-Path $artifacts '真实视频 核心验证.mp4'
    if (!(Test-Path -LiteralPath $VideoPath)) {
        & $ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=640x360:rate=30:duration=90' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=90' -c:v libx264 -preset ultrafast -crf 24 -pix_fmt yuv420p -c:a aac -b:a 96k -shortest -movflags +faststart -y $VideoPath
        if ($LASTEXITCODE -ne 0) { throw 'Test video generation failed' }
    }
}
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
$exe = if ($PlayerPath) { (Resolve-Path -LiteralPath $PlayerPath).Path } else { Join-Path $taskRoot 'NovaPlayer.App/bin/Release/net10.0-windows/NovaPlayer.exe' }
if (!(Test-Path -LiteralPath $exe)) { throw 'Run Build.ps1 first.' }
$summary = @()
for ($run = 1; $run -le $Runs; $run++) {
    # 每轮使用独立报告路径，避免启动失败时误读上一次遗留的报告。
    $report = Join-Path $artifacts "stage1-$run-$([guid]::NewGuid().ToString('N')).json"
    $process = Start-Process -FilePath $exe -ArgumentList '--verify', ('"' + $VideoPath + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Verification timed out; inspect NovaPlayer logs.' }
    $process.WaitForExit()
    if (!(Test-Path -LiteralPath $report)) { throw "No report produced. Exit code: $($process.ExitCode)" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $summary += [pscustomobject]@{ Run = $run; Passed = $result.passed; ExitCode = $process.ExitCode; MeasuredRate = $result.measuredRate; Frames = $result.renderedFrames; Report = $report }
    if ($process.ExitCode -ne 0 -or !$result.passed) { $summary | Format-Table; throw "Verification failed: $report" }
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'stage1-summary.json') -Encoding utf8
$summary | Format-Table -AutoSize
