[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -Path (Join-Path $taskRoot 'licenses/upstream/*') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY.md') -Destination $publish -Force
# 使用两代 PowerShell 都支持的对象属性枚举，避免依赖新版的 AsHashtable 参数。
$deps = Get-Content -LiteralPath (Join-Path $publish 'NovaPlayer.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$packages = @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object Name)
$runtime = Get-Content -LiteralPath (Join-Path $publish 'NovaPlayer.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packages += "$($framework.name).Runtime.win-x64/$($framework.version)"
}
$records = @()
foreach ($package in ($packages | Sort-Object -Unique)) {
    $id, $version = $package.Split('/')
    $source = Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())/$version"
    $nuspec = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { throw "找不到依赖元数据：$package" }
    [xml]$spec = Get-Content -LiteralPath $nuspec.FullName -Raw -Encoding UTF8
    $metadata = $spec.package.metadata
    $destination = Join-Path $output ($id + '-' + $version)
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force
    $documents = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '(?i)license|notice|copying' -and $_.Extension -ne '.nuspec' })
    foreach ($document in $documents) { Copy-Item -LiteralPath $document.FullName -Destination $destination -Force }
    if ($metadata.license.type -eq 'file') { Copy-Item -LiteralPath (Join-Path $source $metadata.license.InnerText) -Destination $destination -Force }
    $expression = $metadata.license.InnerText
    if ($expression -notin @('MIT','Apache-2.0') -and $metadata.license.type -ne 'file') { throw "待审查的新许可证：$package / $expression" }
    $records += [ordered]@{ id = $id; version = $version; license = $expression; licenseType = $metadata.license.type; copyright = [string]$metadata.copyright; repository = [string]$metadata.repository.url; commit = [string]$metadata.repository.commit; nugetSha512 = (Get-Content -LiteralPath (Join-Path $source "$($id.ToLowerInvariant()).$version.nupkg.sha512") -Raw -Encoding UTF8).Trim() }
}
function Read-FfmpegInformation([string]$Argument) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $publish 'native/win-x64/ffmpeg.exe'
    $start.Arguments = $Argument
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw '无法启动 FFmpeg 许可信息读取。' }
        # FFmpeg 会把正常的版本说明写入标准错误流；分别异步读取两条管道，以退出码判断是否失败。
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw '读取 FFmpeg 许可信息超时。' }
        $result = $standardOutput.GetAwaiter().GetResult() + $standardError.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "读取 FFmpeg 许可信息失败，退出码：$($process.ExitCode)。$result" }
        return $result
    } finally { $process.Dispose() }
}
$buildConfiguration = Read-FfmpegInformation '-version'
$binaryLicense = Read-FfmpegInformation '-L'
if ($buildConfiguration -notmatch '--enable-gpl' -or $buildConfiguration -notmatch '--enable-version3' -or $binaryLicense -notmatch 'General Public License') { throw '原生依赖的许可配置改变，需要重新审查。' }
[IO.File]::WriteAllText((Join-Path $output 'ffmpeg-build.txt'), $buildConfiguration + $binaryLicense)
$native = foreach ($name in @('libmpv-2.dll','ffmpeg.exe')) {
    $file = Join-Path $publish "native/win-x64/$name"
    $expected = if ($name -eq 'libmpv-2.dll') { '04e4d27dc385dc18ddd93fbe69c75e635b864f3c6ec53a9122df54c2f7842f4a' } else { 'a1bcba486cce341eb033522f0625bb356ae17b1d124a2bc59cfa22540db81d4b' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "原生运行库发生变化，必须重新审查：$name" }
    @{ name = $name; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; distributionLicense = 'GPL-3.0-or-later（组合构建；包含其他组件许可证）' }
}
$report = [ordered]@{ generatedAtUtc = [DateTime]::UtcNow.ToString('O'); packages = $records; native = @($native); nativeBuildCommit = '1f8d2e76255abc48f0456d9be99b4cab9c6566e6';
    externalDistributionReady = $false; blockers = @('缺少实际原生二进制全部组件的对应源码、修补与构建归档','项目权利人尚未确定与 GPL 组合分发兼容的自有代码发行许可','Windows 10 LTSC / Enterprise 目标环境尚待真实验收') }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'dependency-audit.json') -Encoding utf8
Write-Output "依赖审查已导出：$($records.Count) 个 NuGet/运行时包及 2 个原生文件；对外发行条件尚未满足。"
