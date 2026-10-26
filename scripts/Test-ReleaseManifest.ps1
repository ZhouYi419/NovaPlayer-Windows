[CmdletBinding()]
param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (!$ReleaseDirectory) { $ReleaseDirectory = Join-Path $taskRoot 'artifacts/release/1.0.0-rc.1' }
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$publish = Join-Path $release 'player'
$manifest = Get-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$actual = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
if ($actual.Count -ne $manifest.files.Count) { throw '发布目录与清单的文件数不一致。' }
foreach ($file in $manifest.files) {
    $full = [IO.Path]::GetFullPath((Join-Path $publish $file.path))
    if (!$full.StartsWith($publish + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '清单包含越界路径。' }
    if ((Get-Item -LiteralPath $full).Length -ne $file.size -or (Get-FileHash -LiteralPath $full).Hash -ne $file.sha256) { throw "发布文件校验失败：$($file.path)" }
}
foreach ($line in (Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt'))) {
    $hash, $name = $line -split '  ', 2
    if ((Get-FileHash -LiteralPath (Join-Path $release $name)).Hash -ne $hash) { throw "交付归档校验失败：$name" }
}
$icon = [IO.File]::ReadAllBytes((Join-Path $taskRoot 'NovaPlayer.App/Assets/NovaPlayer.ico'))
if ([BitConverter]::ToUInt16($icon, 2) -ne 1 -or [BitConverter]::ToUInt16($icon, 4) -ne 7) { throw '应用 ICO 格式或尺寸数量不正确。' }
$audit = Get-Content -LiteralPath (Join-Path $publish 'licenses/dependency-audit.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($audit.packages.Count -ne 11 -or $audit.native.Count -ne 2) { throw '分发依赖发生变化，需要重新核对许可证清单。' }
Write-Output "发布文件完整性通过：$($actual.Count) 个文件，七档图标，11 个 NuGet/运行时包和 2 个原生文件；外部发行条件：$($audit.externalDistributionReady)。"
