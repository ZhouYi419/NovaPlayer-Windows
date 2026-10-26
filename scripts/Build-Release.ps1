[CmdletBinding()]
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$release = Join-Path $taskRoot 'artifacts/release/1.0.0-rc.1'
$publish = Join-Path $release 'player'
New-Item -ItemType Directory -Force $publish | Out-Null
if (!$SkipBuild) {
    & "$PSScriptRoot/Build.ps1" -Configuration Release
    & $DotnetCommand restore (Join-Path $taskRoot 'NovaPlayer.App/NovaPlayer.App.csproj') -r win-x64 --configfile (Join-Path $taskRoot 'NuGet.Config') --nologo
    if ($LASTEXITCODE -ne 0) { throw '发布运行时还原失败。' }
    $expectedPublish = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/release/1.0.0-rc.1/player'))
    if ((Resolve-Path -LiteralPath $publish).Path -ne $expectedPublish) { throw '发布清理目录超出预期范围。' }
    Remove-Item -LiteralPath $expectedPublish -Recurse -Force
    New-Item -ItemType Directory -Force $publish | Out-Null
    & $DotnetCommand publish (Join-Path $taskRoot 'NovaPlayer.App/NovaPlayer.App.csproj') -c Release -r win-x64 --self-contained true -o $publish --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'NovaPlayer.exe'))) { throw '缺少发布程序，不能跳过构建。' }
& "$PSScriptRoot/Export-DependencyAudit.ps1" -PublishDirectory $publish
$notes = @'
Nova Player 1.0.0-rc.1 · 内部验收候选版

Windows x64；目标为通用 Windows 10 22H2、LTSC 2021 和 Windows 11，最低安装门槛为 Windows 10 21H2（19044）。
完整目录自包含 .NET Desktop Runtime、libmpv、FFmpeg 与 SQLite，无需联网安装运行时。
“文件关联”只注册打开方式；默认播放器请在 Windows 设置中自行选择。
卸载保留 LocalAppData\NovaPlayer 的播放历史、进度和设置。

当前候选包未签名。Win10 目标机验收、原生依赖完整对应源码分发及项目自身发行许可证尚待完成。
此包用于本地开发验收，不作为对外正式发行包。详见 THIRD_PARTY.md 与 licenses/dependency-audit.json。
'@
[IO.File]::WriteAllText((Join-Path $publish 'RELEASE-NOTES.txt'), $notes, [Text.UTF8Encoding]::new($true))
$compiler = & "$PSScriptRoot/Setup-InstallerTool.ps1"
& $compiler "/DPublishDir=$publish" "/DReleaseDir=$release" (Join-Path $taskRoot 'installer/NovaPlayer.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
$archive = Join-Path $release 'NovaPlayer-1.0.0-rc.1-win-x64-portable.zip'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -Force
# 从 Git 可见的源码生成交付归档；排除未跟踪的用户原型、编译输出与依赖缓存。
$sourceFolder = Join-Path $release ('source-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $sourceFolder | Out-Null
Push-Location $taskRoot
try {
    $sources = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) { throw '无法读取源码归档清单。' }
    foreach ($relative in $sources) {
        if ($relative -match '\.png$' -or !(Test-Path -LiteralPath $relative -PathType Leaf)) { continue }
        $destination = Join-Path $sourceFolder $relative
        New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $relative -Destination $destination
    }
    Compress-Archive -Path (Join-Path $sourceFolder '*') -DestinationPath (Join-Path $release 'NovaPlayer-1.0.0-rc.1-project-source.zip') -Force
} finally { Pop-Location }
$resolvedSource = (Resolve-Path -LiteralPath $sourceFolder).Path
if (!$resolvedSource.StartsWith([IO.Path]::GetFullPath($release) + [IO.Path]::DirectorySeparatorChar + 'source-', [StringComparison]::OrdinalIgnoreCase)) { throw '源码临时目录清理范围异常。' }
Remove-Item -LiteralPath $resolvedSource -Recurse -Force
# 从已解析的目录前缀提取相对路径，兼容 Windows PowerShell 使用的 .NET Framework。
$publishPrefix = (Resolve-Path -LiteralPath $publish).Path.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
    if (!$_.FullName.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '发布清单文件超出目录范围。' }
    @{ path = $_.FullName.Substring($publishPrefix.Length); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ version = '1.0.0-rc.1'; architecture = 'win-x64'; selfContained = $true; externalDistributionReady = $false; signingStatus = [string](Get-AuthenticodeSignature -LiteralPath (Join-Path $release 'NovaPlayer-1.0.0-rc.1-win-x64-setup.exe')).Status; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8
Get-ChildItem -LiteralPath $release -File | Where-Object Extension -in '.exe','.zip' | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Output "候选包、安装器、文件清单及校验和：$release"
