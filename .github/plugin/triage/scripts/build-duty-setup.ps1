$ErrorActionPreference = 'Stop'

$PackageName = 'ellahathaway.buildduty.mcp'
$NuGetSourceName = 'github-ellahathaway'
$NuGetSourceUrl = 'https://nuget.pkg.github.com/ellahathaway/index.json'
$MinimumGhVersion = [Version]'2.66.0'

function Write-Setup([string]$Message) {
    Write-Host "[build-duty setup] $Message"
}

function Throw-Setup([string]$Message) {
    throw "[build-duty setup] Error: $Message"
}

if (Get-Command BuildDuty.Mcp -ErrorAction SilentlyContinue) {
    Write-Setup 'BuildDuty.Mcp already available on PATH. Skipping setup.'
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Throw-Setup 'GitHub CLI (gh) is required. Install gh 2.66.0+ and run setup again.'
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Throw-Setup 'Azure CLI (az) is required. Install Azure CLI and run setup again.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Throw-Setup 'dotnet SDK is required. Install .NET SDK and run setup again.'
}

$GhVersionText = ((gh --version | Select-Object -First 1) -split '\s+')[2].TrimStart('v')
$GhVersion = [Version]$GhVersionText
if ($GhVersion -lt $MinimumGhVersion) {
    Throw-Setup "gh CLI $MinimumGhVersion+ required, you have $GhVersionText. Upgrade gh and retry."
}

if ($LASTEXITCODE -ne 0) {
    Throw-Setup 'Unable to determine gh version. Ensure gh is installed correctly.'
}

gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Throw-Setup 'GitHub CLI is not authenticated. Run: gh auth login'
}

az account show *> $null
if ($LASTEXITCODE -ne 0) {
    Throw-Setup 'Azure CLI is not authenticated. Run: az login'
}

$ExistingSource = dotnet nuget list source | Select-String -SimpleMatch $NuGetSourceName, $NuGetSourceUrl
if (-not $ExistingSource) {
    $GhUser = (gh api user -q .login).Trim()
    if ([string]::IsNullOrWhiteSpace($GhUser)) {
        Throw-Setup 'Unable to determine GitHub username from gh auth.'
    }

    $GhToken = (gh auth token).Trim()
    if ([string]::IsNullOrWhiteSpace($GhToken)) {
        Throw-Setup 'Unable to get GitHub auth token. Run: gh auth refresh --scopes read:packages'
    }

    Write-Setup "Adding NuGet source $NuGetSourceName."
    dotnet nuget add source `
        --username $GhUser `
        --password $GhToken `
        --store-password-in-clear-text `
        --name $NuGetSourceName `
        $NuGetSourceUrl *> $null

    if ($LASTEXITCODE -ne 0) {
        Throw-Setup 'Failed to add GitHub Packages NuGet source.'
    }
}

$ToolInstalled = dotnet tool list --global | Select-String -SimpleMatch $PackageName
if ($ToolInstalled) {
    Write-Setup "Updating $PackageName global tool."
    dotnet tool update --global $PackageName *> $null
}
else {
    Write-Setup "Installing $PackageName global tool."
    dotnet tool install --global $PackageName *> $null
}

if ($LASTEXITCODE -ne 0) {
    Throw-Setup "Failed to install or update global tool $PackageName."
}

if (Get-Command BuildDuty.Mcp -ErrorAction SilentlyContinue) {
    Write-Setup 'BuildDuty.Mcp is ready.'
    exit 0
}

Throw-Setup 'BuildDuty.Mcp installed but not found on PATH. Add ~/.dotnet/tools to PATH and restart your shell.'
