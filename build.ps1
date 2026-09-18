param([string]$DotnetPath = 'dotnet', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $PSScriptRoot
try {
    [xml]$props = Get-Content -LiteralPath 'Directory.Build.props' -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Version is missing from Directory.Build.props.' }
    $publishDir = "artifacts/app-$version"
    & $DotnetPath run --project tests/DeltaPlayer.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Publish cancelled.' }
    if (-not $SkipPublish) {
        & $DotnetPath publish src/DeltaPlayer.csproj -c Release -r win-x64 --self-contained true -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Write-Host "Ready: $publishDir/DeltaPlayer.exe"
    }
} finally { Pop-Location }

