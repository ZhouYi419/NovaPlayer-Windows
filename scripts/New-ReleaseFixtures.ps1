[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [ValidateRange(10,86400)][int]$SoakSeconds = 120)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$ffmpeg = Join-Path $taskRoot 'native/win-x64/ffmpeg.exe'
$folder = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $folder | Out-Null
$cases = [Collections.Generic.List[object]]::new()
function Add-Fixture([string]$Name, [string]$Codec, [int]$Width, [int]$Height, [string[]]$Options) {
    $file = Join-Path $folder $Name
    & $ffmpeg -hide_banner -loglevel error @Options -threads 4 -y $file
    if ($LASTEXITCODE -ne 0) { throw "样片生成失败：$Name" }
    $cases.Add(@{ Path = $file; Codec = $Codec; Width = $Width; Height = $Height })
}
$base = Join-Path $folder '中文 空格 H264.mp4'
Add-Fixture '中文 空格 H264.mp4' 'h264' 640 360 @('-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=40','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=40','-c:v','libx264','-preset','ultrafast','-crf','25','-pix_fmt','yuv420p','-c:a','aac','-shortest','-movflags','+faststart')
foreach ($ext in @('mov','m4v','ts','m2ts')) { Add-Fixture "封装.$ext" 'h264' 640 360 @('-i',$base,'-c','copy') }
Add-Fixture 'MPEG4.avi' 'mpeg4' 640 360 @('-i',$base,'-c:v','mpeg4','-q:v','5','-c:a','libmp3lame')
Add-Fixture 'WindowsMedia.wmv' 'wmv2' 640 360 @('-i',$base,'-c:v','wmv2','-b:v','1000k','-c:a','wmav2')
Add-Fixture 'Flash.flv' 'flv1' 640 360 @('-i',$base,'-c:v','flv','-b:v','1000k','-c:a','libmp3lame','-ar','44100')
Add-Fixture 'MPEG2.mpg' 'mpeg2video' 640 360 @('-i',$base,'-c:v','mpeg2video','-q:v','5','-c:a','mp2','-f','mpeg')
Copy-Item -LiteralPath (Join-Path $folder 'MPEG2.mpg') -Destination (Join-Path $folder 'MPEG2.mpeg')
$cases.Add(@{ Path = (Join-Path $folder 'MPEG2.mpeg'); Codec = 'mpeg2video'; Width = 640; Height = 360 })
Add-Fixture 'HEVC 10bit.mkv' 'hevc' 640 360 @('-i',$base,'-c:v','libx265','-preset','ultrafast','-x265-params','pools=4:log-level=error','-pix_fmt','yuv420p10le','-crf','30','-c:a','copy')
Add-Fixture 'VP9 Opus.webm' 'vp9' 640 360 @('-i',$base,'-c:v','libvpx-vp9','-deadline','realtime','-cpu-used','8','-row-mt','1','-crf','40','-b:v','0','-c:a','libopus')
Add-Fixture 'AV1.mkv' 'av1' 640 360 @('-i',$base,'-c:v','libsvtav1','-preset','12','-crf','45','-svtav1-params','lp=4','-c:a','copy')
Add-Fixture '竖屏.mkv' 'h264' 360 640 @('-f','lavfi','-i','testsrc2=size=360x640:rate=30:duration=40','-c:v','libx264','-preset','ultrafast','-crf','28')
Add-Fixture 'FullHD.mp4' 'h264' 1920 1080 @('-f','lavfi','-i','testsrc2=size=1920x1080:rate=30:duration=30','-c:v','libx264','-preset','ultrafast','-crf','30')
Add-Fixture 'UltraHD.mp4' 'h264' 3840 2160 @('-f','lavfi','-i','testsrc2=size=3840x2160:rate=30:duration=30','-c:v','libx264','-preset','ultrafast','-crf','34')
$plan = Join-Path $folder 'plan.json'
@{ Cases = $cases.ToArray(); SoakSeconds = $SoakSeconds } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $plan -Encoding utf8
Write-Output $plan
