$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools/packages'
$localDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $DotnetCommand = $localDotnet
    $env:DOTNET_ROOT = Split-Path $localDotnet -Parent
} else {
    $DotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
}
