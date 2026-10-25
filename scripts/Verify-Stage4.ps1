[CmdletBinding()]
param([string]$VideoPath, [string]$PlayerPath)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
if (!$VideoPath) { $VideoPath = Join-Path $taskRoot 'artifacts/真实视频 核心验证.mp4' }
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
$runFolder = Join-Path $taskRoot "artifacts/stage4-$([guid]::NewGuid().ToString('N'))"
$fixtures = Join-Path $runFolder 'fixtures'
New-Item -ItemType Directory -Force $fixtures | Out-Null
$subtitle = "1`r`n00:00:00,000 --> 00:01:25,000`r`nNova Player 真实字幕 · 中文验证`r`n"
$english = "1`r`n00:00:00,000 --> 00:01:25,000`r`nNova Player English subtitle`r`n"
[System.IO.File]::WriteAllText((Join-Path $fixtures '内嵌中文.srt'), $subtitle)
[System.IO.File]::WriteAllText((Join-Path $fixtures '内嵌英文.srt'), $english)
$file = Join-Path $fixtures '多轨 中文样片.mkv'
$ffmpeg = Join-Path $taskRoot 'native/win-x64/ffmpeg.exe'
& $ffmpeg -hide_banner -loglevel error -i $VideoPath -f lavfi -i 'sine=frequency=880:sample_rate=48000:duration=90' -i (Join-Path $fixtures '内嵌中文.srt') -i (Join-Path $fixtures '内嵌英文.srt') -map 0:v:0 -map 0:a:0 -map 1:a:0 -map 2:0 -map 3:0 -c:v copy -c:a aac -c:s srt -metadata:s:a:0 'title=原始音轨' -metadata:s:a:0 language=jpn -metadata:s:a:1 'title=English Audio' -metadata:s:a:1 language=eng -metadata:s:s:0 'title=中文字幕' -metadata:s:s:0 language=zho -metadata:s:s:1 'title=English Subtitle' -metadata:s:s:1 language=eng -t 90 -y $file
if ($LASTEXITCODE -ne 0) { throw '多轨测试样片生成失败。' }
[System.IO.File]::WriteAllText((Join-Path $fixtures '多轨 中文样片.srt'), $subtitle)
[System.IO.File]::WriteAllText((Join-Path $fixtures '外挂字幕.srt'), $english)
[System.IO.File]::WriteAllText((Join-Path $fixtures '损坏字幕.srt'), '这不是合法的字幕时间轴文件。')
$ass = @'
[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,30,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,24,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:01:25.00,Default,,0,0,0,,Nova Player ASS subtitle
'@
[System.IO.File]::WriteAllText((Join-Path $fixtures '外挂字幕.ass'), $ass)
$ssa = @'
[Script Info]
ScriptType: v4.00
[V4 Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding
Style: Default,Arial,30,16777215,255,0,0,0,0,1,2,0,2,10,10,24,0,1
[Events]
Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: Marked=0,0:00:00.00,0:01:25.00,Default,,0,0,0,,Nova Player SSA subtitle
'@
[System.IO.File]::WriteAllText((Join-Path $fixtures '外挂字幕.ssa'), $ssa)
Copy-Item -LiteralPath $VideoPath -Destination (Join-Path $fixtures '无字幕.mp4')
$exe = if ($PlayerPath) { (Resolve-Path -LiteralPath $PlayerPath).Path } else { Join-Path $taskRoot 'NovaPlayer.App/bin/Release/net10.0-windows/NovaPlayer.App.exe' }
foreach ($phase in @('experience', 'experience-reopen')) {
    $report = Join-Path $runFolder "$phase.json"
    $process = Start-Process -FilePath $exe -ArgumentList "--verify-$phase", ('"' + $file + '"'), ('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(90000)) { $process.Kill(); throw "第四阶段 $phase 验收超时。" }
    $process.WaitForExit()
    if (!(Test-Path -LiteralPath $report)) { throw "未生成报告：$report，退出码：$($process.ExitCode)" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Write-Output "$phase ：$(@($result.checks | Where-Object passed).Count) / $($result.checks.Count)，报告：$report"
    if ($process.ExitCode -ne 0 -or !$result.passed) { Write-Output ($result | ConvertTo-Json -Depth 10); throw '第四阶段验收失败。' }
}
Write-Output "第四阶段档案和界面导出：$runFolder"
