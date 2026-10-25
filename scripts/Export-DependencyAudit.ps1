[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -Path (Join-Path $taskRoot 'licenses/upstream/*') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY.md') -Destination $publish -Force
$deps = Get-Content -LiteralPath (Join-Path $publish 'NovaPlayer.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$packages = @($deps.libraries.GetEnumerator() | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object Key)
$runtime = Get-Content -LiteralPath (Join-Path $publish 'NovaPlayer.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packages += "$($framework.name).Runtime.win-x64/$($framework.version)"
}
$records = @()
foreach ($package in ($packages | Sort-Object -Unique)) {
    $id, $version = $package.Split('/')
    $source = Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())/$version"
    $nuspec = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' | Select-Object -First 1
    if (!$nuspec) { throw "找不到依赖元数据：$package" }
    [xml]$spec = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $spec.package.metadata
    $destination = Join-Path $output ($id + '-' + $version)
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force
    $documents = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '(?i)license|notice|copying' -and $_.Extension -ne '.nuspec' })
    foreach ($document in $documents) { Copy-Item -LiteralPath $document.FullName -Destination $destination -Force }
    if ($metadata.license.type -eq 'file') { Copy-Item -LiteralPath (Join-Path $source $metadata.license.InnerText) -Destination $destination -Force }
    $expression = $metadata.license.InnerText
    if ($expression -notin @('MIT','Apache-2.0') -and $metadata.license.type -ne 'file') { throw "待审查的新许可证：$package / $expression" }
    $records += [ordered]@{ id = $id; version = $version; license = $expression; licenseType = $metadata.license.type; copyright = [string]$metadata.copyright; repository = [string]$metadata.repository.url; commit = [string]$metadata.repository.commit; nugetSha512 = (Get-Content -LiteralPath (Join-Path $source "$($id.ToLowerInvariant()).$version.nupkg.sha512") -Raw).Trim() }
}
$buildConfiguration = & (Join-Path $publish 'native/win-x64/ffmpeg.exe') -version 2>&1 | Out-String
$binaryLicense = & (Join-Path $publish 'native/win-x64/ffmpeg.exe') -L 2>&1 | Out-String
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
