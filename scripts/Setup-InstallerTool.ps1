[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $taskRoot '.tools/inno/ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne '0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7') { throw '本地 Inno Setup 编译器不匹配固定版本。' }
    Write-Output $compiler; return
}
New-Item -ItemType Directory -Force (Join-Path $taskRoot '.tools/downloads') | Out-Null
$download = Join-Path $taskRoot '.tools/downloads/innosetup-6.7.3.exe'
$hash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (!(Test-Path -LiteralPath $download) -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $hash) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $hash) { throw 'Inno Setup 下载校验失败。' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') { throw 'Inno Setup 官方签名验证失败。' }
# 编译工具仅安装到开发目录，不打包进播放器，也不改变系统默认应用。
$process = Start-Process -FilePath $download -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="' + (Join-Path $taskRoot '.tools/inno') + '"'),'/NOICONS','/TASKS=' -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { throw 'Inno Setup 工具安装超时。' }
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 工具安装失败。' }
Write-Output $compiler
