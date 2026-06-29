#Requires -Version 5.1

<#
.SYNOPSIS
    Syncs the canonical build-duty setup scripts into each plugin's scripts/ directory.
.DESCRIPTION
    Copilot CLI installs only each plugin's own source subtree, so the setup script
    that every plugin's hooks.json invokes must be bundled inside that plugin. To avoid
    hand-maintaining four copies, the canonical scripts live in eng/plugin-scripts/ and
    this helper copies them into each plugin's scripts/ directory.

    Run without arguments to (re)generate the per-plugin copies. Run with -Check to verify
    that every per-plugin copy is byte-identical to the canonical source without modifying
    anything; it exits 1 when any copy is missing or has drifted (used in CI).
.PARAMETER Check
    Verify-only mode. Reports drift and exits 1 instead of copying.
.EXAMPLE
    pwsh eng/sync-plugin-scripts.ps1
    Regenerates the per-plugin copies.
.EXAMPLE
    pwsh eng/sync-plugin-scripts.ps1 -Check
    Fails (exit 1) if any per-plugin copy is out of sync with the canonical source.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$CanonicalDir = Join-Path $RepoRoot 'eng/plugin-scripts'
$PluginRoot = Join-Path $RepoRoot '.github/plugin'

$Plugins = @('triage', 'config-management', 'remediation', 'reporting')
$ScriptFiles = @('build-duty-setup.ps1', 'build-duty-setup.sh')

function Get-FileBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return [System.IO.File]::ReadAllBytes($Path)
}

function Test-BytesEqual($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return $false }
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) {
        if ($a[$i] -ne $b[$i]) { return $false }
    }
    return $true
}

# Validate canonical sources exist before doing anything.
foreach ($file in $ScriptFiles) {
    $canonicalPath = Join-Path $CanonicalDir $file
    if (-not (Test-Path -LiteralPath $canonicalPath)) {
        throw "Canonical script not found: $canonicalPath"
    }
}

$drift = [System.Collections.Generic.List[string]]::new()
$copied = [System.Collections.Generic.List[string]]::new()

foreach ($plugin in $Plugins) {
    $destDir = Join-Path $PluginRoot (Join-Path $plugin 'scripts')

    if (-not $Check -and -not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    foreach ($file in $ScriptFiles) {
        $src = Join-Path $CanonicalDir $file
        $dest = Join-Path $destDir $file
        $relDest = ".github/plugin/$plugin/scripts/$file"

        $srcBytes = Get-FileBytes $src
        $destBytes = Get-FileBytes $dest

        if (Test-BytesEqual $srcBytes $destBytes) {
            continue
        }

        if ($Check) {
            if ($null -eq $destBytes) {
                $drift.Add("$relDest is missing")
            }
            else {
                $drift.Add("$relDest differs from canonical eng/plugin-scripts/$file")
            }
        }
        else {
            Copy-Item -LiteralPath $src -Destination $dest -Force
            $copied.Add($relDest)
        }
    }
}

if ($Check) {
    if ($drift.Count -gt 0) {
        Write-Host 'Plugin setup scripts are OUT OF SYNC with eng/plugin-scripts/:' -ForegroundColor Red
        foreach ($d in $drift) { Write-Host "  - $d" -ForegroundColor Red }
        Write-Host ''
        Write-Host 'Run: pwsh eng/sync-plugin-scripts.ps1' -ForegroundColor Yellow
        exit 1
    }
    Write-Host 'All plugin setup scripts are in sync with eng/plugin-scripts/.' -ForegroundColor Green
    exit 0
}

if ($copied.Count -gt 0) {
    Write-Host 'Synced plugin setup scripts:' -ForegroundColor Green
    foreach ($c in $copied) { Write-Host "  - $c" }
    Write-Host ''
    Write-Host 'If any .sh copies are newly created, ensure the executable bit is set:' -ForegroundColor Yellow
    Write-Host '  git add --chmod=+x .github/plugin/*/scripts/build-duty-setup.sh' -ForegroundColor Yellow
}
else {
    Write-Host 'Plugin setup scripts already up to date.' -ForegroundColor Green
}
