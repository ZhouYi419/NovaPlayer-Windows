[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [switch]$Publish)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Use-DevelopmentEnvironment.ps1"
Push-Location $taskRoot
try {
    & $DotnetCommand restore NovaPlayer.sln --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $DotnetCommand build NovaPlayer.sln -c $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & $DotnetCommand test tests/NovaPlayer.Tests -c $Configuration --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    if ($Publish) {
        & $DotnetCommand publish NovaPlayer.App/NovaPlayer.App.csproj -c $Configuration -r win-x64 --self-contained true -o artifacts/player --configfile NuGet.Config --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    }
} finally { Pop-Location }
