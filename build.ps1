param([string]$DotnetPath = 'dotnet', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    & $DotnetPath run --project tests/DeltaPlayer.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Publish cancelled.' }
    if (-not $SkipPublish) {
        & $DotnetPath publish src/DeltaPlayer.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Write-Host 'Ready: artifacts/app/DeltaPlayer.exe'
    }
} finally { Pop-Location }
