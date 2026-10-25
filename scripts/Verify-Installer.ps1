[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallerPath, [Parameter(Mandatory)][string]$VideoPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$video = (Resolve-Path -LiteralPath $VideoPath).Path
$run = Join-Path $taskRoot "artifacts/installer-$([guid]::NewGuid().ToString('N'))"
$install = Join-Path $run '中文 安装目录'
New-Item -ItemType Directory -Force $run | Out-Null
$extensions = @('mp4','mkv','avi','mov','webm','wmv','flv','m4v','ts','m2ts','mpg','mpeg')
$ownKeys = @('Software\Classes\NovaPlayer.Video','Software\NovaPlayer\Capabilities','Software\Microsoft\Windows\CurrentVersion\Uninstall\{88ACF80B-19A8-4F10-9C69-E8336AE516CF}_is1')
foreach ($key in $ownKeys) { if (Test-Path -LiteralPath "HKCU:\$key") { throw "检测到已有 Nova Player 注册项，停止隔离安装测试以保护现有安装：$key" } }
$registered = Get-Item 'HKCU:\Software\RegisteredApplications' -ErrorAction SilentlyContinue
if ($registered -and $registered.GetValue('Nova Player')) { throw '检测到已有默认应用候选注册，停止隔离测试。' }
$defaults = @{}
function Read-Association([string]$Ext) {
    $class = Get-Item -LiteralPath "HKCU:\Software\Classes\.$Ext" -ErrorAction SilentlyContinue
    $choice = Get-Item -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.$Ext\UserChoice" -ErrorAction SilentlyContinue
    return @{ extensionDefault = $(if ($class) { $class.GetValue('') } else { $null }); userChoice = $(if ($choice) { $choice.GetValue('ProgId') } else { $null }); hash = $(if ($choice) { $choice.GetValue('Hash') } else { $null }) } | ConvertTo-Json -Compress
}
foreach ($ext in $extensions) {
    $defaults[$ext] = Read-Association $ext
    $openWith = Get-Item -LiteralPath "HKCU:\Software\Classes\.$ext\OpenWithProgids" -ErrorAction SilentlyContinue
    if ($openWith -and $openWith.GetValueNames() -contains 'NovaPlayer.Video') { throw "检测到已有 .$ext 播放器候选，停止隔离测试。" }
}
$checks = [Collections.Generic.List[object]]::new()
$passed = $false
$failure = $null
$installed = $false
function Check([string]$Name, [bool]$Ok) { $checks.Add(@{ name = $Name; passed = $Ok }); if (!$Ok) { throw $Name } }
function Run-Setup([string]$Exe, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw '安装或卸载超时。' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "安装器退出码：$($process.ExitCode)" }
}
try {
    Run-Setup $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $install + '"'),'/TASKS=fileassociations',('/LOG="' + (Join-Path $run 'install.log') + '"'))
    $installed = $true
    $exe = Join-Path $install 'NovaPlayer.exe'
    Check '无管理员安装到中文空格路径' (Test-Path -LiteralPath $exe)
    Check '自包含运行时、播放内核、缩略图工具与许可清单完整' ((Test-Path (Join-Path $install 'coreclr.dll')) -and (Test-Path (Join-Path $install 'native/win-x64/libmpv-2.dll')) -and (Test-Path (Join-Path $install 'native/win-x64/ffmpeg.exe')) -and (Test-Path (Join-Path $install 'licenses/dependency-audit.json')))
    $command = (Get-Item 'HKCU:\Software\Classes\NovaPlayer.Video\shell\open\command').GetValue('')
    Check '文件打开命令正确引用程序和媒体路径' ($command -eq ('"' + $exe + '" "%1"'))
    foreach ($ext in $extensions) {
        $key = Get-Item -LiteralPath "HKCU:\Software\Classes\.$ext\OpenWithProgids"
        Check "注册 .$ext 候选且保留原默认应用" (($key.GetValueNames() -contains 'NovaPlayer.Video') -and (Read-Association $ext) -eq $defaults[$ext])
    }
    # 从实际注册的命令模板展开媒体路径；尾部验收参数仅隔离数据目录、检查真实播放并自动退出。
    foreach ($round in @(1,2)) {
        $report = Join-Path $run "launch-$round.json"
        $argumentString = $command.Substring(('"' + $exe + '" ').Length).Replace('%1', $video) + ' --verify-file-open "' + $report + '"'
        $process = Start-Process -FilePath $exe -ArgumentList $argumentString -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(45000)) { $process.Kill(); throw '关联文件播放验收超时。' }
        $process.WaitForExit()
        if (!(Test-Path -LiteralPath $report)) { throw "缺少文件打开报告：$report" }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Check "安装版关联命令真实播放 / 第 $round 次" ($process.ExitCode -eq 0 -and $result.passed)
        if ($round -eq 2) { Check '安装版跨进程恢复12秒进度' ($result.resumedPosition -ge 11 -and $result.resumedPosition -lt 16) }
    }
    Run-Setup $installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + $install + '"'),'/TASKS=fileassociations')
    Check '同版本覆盖安装保持隔离数据库' (Test-Path -LiteralPath (Join-Path $run 'launch-profile/data/player.db'))
    Run-Setup (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $run 'uninstall.log') + '"'))
    $installed = $false
    Check '卸载移除程序并保留播放档案' (!(Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath (Join-Path $run 'launch-profile/data/player.db')))
    foreach ($key in $ownKeys) { Check "卸载移除自有注册项：$key" (!(Test-Path -LiteralPath "HKCU:\$key")) }
    $registered = Get-Item 'HKCU:\Software\RegisteredApplications' -ErrorAction SilentlyContinue
    Check '卸载移除默认应用候选入口' (!$registered -or !$registered.GetValue('Nova Player'))
    foreach ($ext in $extensions) {
        $key = Get-Item -LiteralPath "HKCU:\Software\Classes\.$ext\OpenWithProgids" -ErrorAction SilentlyContinue
        Check "卸载移除 .$ext 候选且保留原默认应用" ((!$key -or $key.GetValueNames() -notcontains 'NovaPlayer.Video') -and (Read-Association $ext) -eq $defaults[$ext])
    }
    $passed = $true
} catch { $failure = $_.ToString(); throw } finally {
    if ($installed -and (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))) {
        try { Run-Setup (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') } catch { Write-Warning "验收安装仍需手动卸载：$install" }
    }
    @{ passed = $passed; failure = $failure; os = [Environment]::OSVersion.Version.ToString(); installerSha256 = (Get-FileHash -LiteralPath $installer).Hash; checks = $checks.ToArray() } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'installer.json') -Encoding utf8
    Write-Output "安装 / 关联 / 卸载验收：$run"
}
