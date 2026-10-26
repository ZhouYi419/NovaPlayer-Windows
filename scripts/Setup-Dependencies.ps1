[CmdletBinding()]
param([switch]$IncludeVerificationTools)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Force $tools | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Hash, [string]$Algorithm = 'SHA256') {
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) {
        Invoke-WebRequest -Uri $Url -OutFile $Path
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ne $Hash) { throw "Checksum mismatch: $Path" }
}

$sdk = Join-Path $tools 'dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) {
    $zip = Join-Path $tools 'dotnet-sdk.zip'
    Get-VerifiedFile 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip' $zip 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd' 'SHA512'
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tools 'dotnet') -Force
}

$extractor = Join-Path $tools '7zr.exe'
Get-VerifiedFile 'https://www.7-zip.org/a/7zr.exe' $extractor '256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282'
$archive = Join-Path $tools 'mpv-dev.7z'
Get-VerifiedFile 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261021/mpv-dev-x86_64-20261021-git-e471496c56.7z' $archive 'efc719eff3dc5954b4a5efc800632808bcec01cc9034940dd14e5eb7fe9ea633'
& $extractor x $archive "-o$(Join-Path $tools 'mpv')" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'libmpv extraction failed' }
$native = Join-Path $taskRoot 'native/win-x64'
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item -LiteralPath (Join-Path $tools 'mpv/libmpv-2.dll') -Destination (Join-Path $native 'libmpv-2.dll') -Force
if ((Get-FileHash -LiteralPath (Join-Path $native 'libmpv-2.dll')).Hash -ne '04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A') { throw 'libmpv DLL checksum mismatch' }

# 保留旧参数以兼容已有命令；第三阶段起 FFmpeg 也是缩略图运行依赖，始终部署。
$archive = Join-Path $tools 'ffmpeg.7z'
Get-VerifiedFile 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261021/ffmpeg-x86_64-git-9a861e968.7z' $archive 'fc9c135b799b410ffc78517f428904c299bc64cd8144f21f63f84fcd8c35ea19'
& $extractor x $archive "-o$(Join-Path $tools 'ffmpeg')" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg extraction failed' }
Copy-Item -LiteralPath (Join-Path $tools 'ffmpeg/ffmpeg.exe') -Destination (Join-Path $native 'ffmpeg.exe') -Force
Write-Output 'Dependencies ready. SDK/runtime files are local to this workspace.'
