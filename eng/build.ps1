[CmdletBinding()]
param(
    [switch]$Pack,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $RepoRoot 'BuildDuty.slnx'
$Artifacts = Join-Path $RepoRoot 'artifacts'

if ($Install) {
    $Pack = $true
}

Write-Host '==> Clean'
Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Include bin, obj -Directory -Recurse | Remove-Item -Recurse -Force

Write-Host '==> Restore'
dotnet restore $Solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '==> Build'
dotnet build $Solution --no-restore -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '==> Test'
dotnet test $Solution --no-build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Pack) {
    Write-Host '==> Pack'
    $PackagesDir = Join-Path $Artifacts 'packages'
    dotnet pack $Solution --no-build -c Release -o $PackagesDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Package(s) written to $PackagesDir"
}

if ($Install) {
    Write-Host '==> Install (MCP server global tool)'
    & { $ErrorActionPreference = 'SilentlyContinue'; dotnet tool uninstall -g ellahathaway.buildduty.mcp 2>&1 | Out-Null }
    $LocalPackages = Join-Path $Artifacts 'packages'
    $LocalConfig = Join-Path $Artifacts 'nuget.local.config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-artifacts" value="$LocalPackages" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $LocalConfig -Encoding utf8

    try {
        dotnet tool install --global --configfile $LocalConfig ellahathaway.buildduty.mcp --prerelease
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Remove-Item -Path $LocalConfig -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '==> Done'
