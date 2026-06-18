#requires -Version 5.1
<#
.SYNOPSIS
    Materialize the repo-local skill symlinks (.github/skills) on Windows.

.DESCRIPTION
    build-duty keeps a single source of truth for each agent skill under
    .github/plugin/<plugin>/skills/<skill>/. To also make the skills
    auto-discoverable when working *inside this repo* (GitHub Copilot CLI,
    VS Code, and other clients scan .github/skills), the repo commits
    .github/skills/<skill> as git symlinks that point at the owning plugin's
    skill directory.

    Git creates those symlinks as real symlinks automatically on Linux and
    macOS (core.symlinks defaults to true). On Windows, git only does so when
    core.symlinks is enabled, which requires Developer Mode or an elevated
    checkout. When it is not enabled, git writes each symlink as a small text
    stub instead of a directory, and the skills are not discovered.

    This script enables core.symlinks for the local clone and re-checks-out
    .github/skills so the symlinks materialize. If the OS still refuses to
    create symlinks (no Developer Mode and not elevated), it prints guidance
    and leaves the working tree clean — the marketplace plugin install path
    still provides the skills regardless.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$skillsDir = Join-Path $RepoRoot '.github/skills'
if (-not (Test-Path $skillsDir)) {
    return
}

function Test-SkillsMaterialized {
    $entries = Get-ChildItem -LiteralPath $skillsDir -Force -ErrorAction SilentlyContinue
    if (-not $entries) {
        return $false
    }
    foreach ($entry in $entries) {
        if (-not (Test-Path (Join-Path $entry.FullName 'SKILL.md'))) {
            return $false
        }
    }
    return $true
}

# Already real symlinks (Linux/macOS, or Windows with Developer Mode): nothing to do.
if (Test-SkillsMaterialized) {
    return
}

$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) {
    Write-Warning 'Repo-local skills under .github/skills are not materialized and git is unavailable to repair them. Install the marketplace plugins to use the skills.'
    return
}

Push-Location $RepoRoot
try {
    if ((git rev-parse --is-inside-work-tree 2>$null) -ne 'true') {
        Write-Warning 'Not a git work tree; cannot materialize .github/skills symlinks. Install the marketplace plugins to use the skills.'
        return
    }

    Write-Host '==> Materializing repo-local skill symlinks (.github/skills)'
    git config core.symlinks true | Out-Null
    git checkout -- .github/skills 2>$null

    if (Test-SkillsMaterialized) {
        Write-Host '    Skills materialized.'
    }
    else {
        Write-Warning @'
Could not create symlinks for .github/skills (Windows symlink creation is disabled).
Enable Developer Mode (Settings > System > For developers) or run from an elevated
shell, then re-run the build. The skills remain available via the marketplace
plugins regardless.
'@
    }
}
finally {
    Pop-Location
}
