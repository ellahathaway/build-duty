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
    Write-Host '==> Install (global tool)'
    & { $ErrorActionPreference = 'SilentlyContinue'; dotnet tool uninstall -g buildduty 2>&1 | Out-Null }
    dotnet tool install --global --add-source (Join-Path $Artifacts 'packages') buildduty
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host '==> Done'
