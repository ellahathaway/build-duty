#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

<#
.SYNOPSIS
    Structural validation tests for sessionStart hook wiring.
.DESCRIPTION
    Verifies that all plugins reference a valid hooks.json, each hooks.json
    is well-formed, and the referenced scripts exist.
#>

BeforeAll {
    $PluginRoot = Join-Path $PSScriptRoot '..' '..'
    $ScriptsDir = Join-Path $PluginRoot 'scripts'
    $PluginDirs = @('triage', 'config-management', 'remediation', 'reporting')
}

Describe 'Plugin hook wiring' {

    Context 'All plugins reference hooks.json' {
        It '<pluginName>/plugin.json contains a "hooks" field' -ForEach @(
            @{ pluginName = 'triage' }
            @{ pluginName = 'config-management' }
            @{ pluginName = 'remediation' }
            @{ pluginName = 'reporting' }
        ) {
            $pluginJsonPath = Join-Path $PluginRoot $pluginName 'plugin.json'
            $pluginJsonPath | Should -Exist
            $content = Get-Content $pluginJsonPath -Raw | ConvertFrom-Json
            $content.hooks | Should -Be 'hooks.json'
        }
    }

    Context 'hooks.json files are valid' {
        It '<pluginName>/hooks.json is valid JSON with skillInvocation array' -ForEach @(
            @{ pluginName = 'triage' }
            @{ pluginName = 'config-management' }
            @{ pluginName = 'remediation' }
            @{ pluginName = 'reporting' }
        ) {
            $hooksPath = Join-Path $PluginRoot $pluginName 'hooks.json'
            $hooksPath | Should -Exist
            $hooks = Get-Content $hooksPath -Raw | ConvertFrom-Json
            $hooks.version | Should -Be 1
            $hooks.hooks.skillInvocation | Should -Not -BeNullOrEmpty
            $hooks.hooks.skillInvocation.Count | Should -BeGreaterOrEqual 1
        }

        It '<pluginName>/hooks.json skillInvocation entry has required fields' -ForEach @(
            @{ pluginName = 'triage' }
            @{ pluginName = 'config-management' }
            @{ pluginName = 'remediation' }
            @{ pluginName = 'reporting' }
        ) {
            $hooksPath = Join-Path $PluginRoot $pluginName 'hooks.json'
            $hook = (Get-Content $hooksPath -Raw | ConvertFrom-Json).hooks.skillInvocation[0]
            $hook.type | Should -Be 'command'
            $hook.bash | Should -Not -BeNullOrEmpty
            $hook.powershell | Should -Not -BeNullOrEmpty
            $hook.timeoutSec | Should -BeGreaterThan 0
        }
    }

    Context 'Referenced scripts exist' {
        It 'build-duty-setup.ps1 exists' {
            Join-Path $ScriptsDir 'build-duty-setup.ps1' | Should -Exist
        }

        It 'build-duty-setup.sh exists' {
            Join-Path $ScriptsDir 'build-duty-setup.sh' | Should -Exist
        }
    }

    Context 'Script relative paths resolve correctly from each plugin' {
        It '<pluginName> bash path resolves to existing script' -ForEach @(
            @{ pluginName = 'triage' }
            @{ pluginName = 'config-management' }
            @{ pluginName = 'remediation' }
            @{ pluginName = 'reporting' }
        ) {
            $hooksPath = Join-Path $PluginRoot $pluginName 'hooks.json'
            $hook = (Get-Content $hooksPath -Raw | ConvertFrom-Json).hooks.skillInvocation[0]
            $resolvedBash = Join-Path $PluginRoot $pluginName $hook.bash
            $resolvedBash = [System.IO.Path]::GetFullPath($resolvedBash)
            $resolvedBash | Should -Exist
        }

        It '<pluginName> powershell path resolves to existing script' -ForEach @(
            @{ pluginName = 'triage' }
            @{ pluginName = 'config-management' }
            @{ pluginName = 'remediation' }
            @{ pluginName = 'reporting' }
        ) {
            $hooksPath = Join-Path $PluginRoot $pluginName 'hooks.json'
            $hook = (Get-Content $hooksPath -Raw | ConvertFrom-Json).hooks.skillInvocation[0]
            $resolvedPs = Join-Path $PluginRoot $pluginName $hook.powershell
            $resolvedPs = [System.IO.Path]::GetFullPath($resolvedPs)
            $resolvedPs | Should -Exist
        }
    }

    Context 'hooks.json content is identical across all plugins' {
        It 'All plugins share the same hooks.json content' {
            $referenceContent = Get-Content (Join-Path $PluginRoot 'triage' 'hooks.json') -Raw
            foreach ($plugin in @('config-management', 'remediation', 'reporting')) {
                $content = Get-Content (Join-Path $PluginRoot $plugin 'hooks.json') -Raw
                $content | Should -Be $referenceContent -Because "$plugin/hooks.json should match triage/hooks.json"
            }
        }
    }
}
