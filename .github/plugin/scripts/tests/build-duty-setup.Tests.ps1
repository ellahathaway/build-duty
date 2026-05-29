#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

<#
.SYNOPSIS
    Tests for .github/plugin/scripts/build-duty-setup.ps1
.DESCRIPTION
    Validates idempotency, prerequisite checks, version gating, NuGet source
    configuration, and dotnet tool install/update paths by mocking external commands.
#>

BeforeAll {
    $ScriptPath = Join-Path $PSScriptRoot '..' 'build-duty-setup.ps1'
}

Describe 'build-duty-setup.ps1' {

    Context 'Early exit when BuildDuty.Mcp already on PATH' {
        BeforeAll {
            Mock Get-Command { return [PSCustomObject]@{ Name = 'BuildDuty.Mcp' } } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
        }

        It 'Exits 0 and prints Skipping setup' {
            # Write-Host goes to Information stream (6) - capture via transcript
            $output = powershell -NoProfile -Command "& '$ScriptPath'" 2>&1
            $LASTEXITCODE | Should -Be 0
            ($output -join "`n") | Should -Match 'Skipping setup'
        }
    }

    Context 'Missing prerequisites' {
        BeforeAll {
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
        }

        It 'Fails when gh is not installed' {
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'gh' }
            { & $ScriptPath } | Should -Throw '*GitHub CLI (gh) is required*'
        }

        It 'Fails when az is not installed' {
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'az' }
            { & $ScriptPath } | Should -Throw '*Azure CLI (az) is required*'
        }

        It 'Fails when dotnet is not installed' {
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'az' } } -ParameterFilter { $Name -eq 'az' }
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'dotnet' }
            { & $ScriptPath } | Should -Throw '*dotnet SDK is required*'
        }
    }

    Context 'gh version check' {
        BeforeAll {
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'az' } } -ParameterFilter { $Name -eq 'az' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' }
        }

        It 'Fails when gh version is below minimum' {
            Mock gh { 'gh version 2.14.4 (2022-01-01)' } -ParameterFilter { $args[0] -eq '--version' }
            { & $ScriptPath } | Should -Throw '*2.66.0+ required*you have 2.14.4*'
        }

        It 'Fails when gh version cannot be parsed' {
            Mock gh { 'unknown output' } -ParameterFilter { $args[0] -eq '--version' }
            { & $ScriptPath } | Should -Throw '*Unable to determine gh version*'
        }
    }

    Context 'Auth checks' {
        BeforeAll {
            Mock Get-Command { $null } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'az' } } -ParameterFilter { $Name -eq 'az' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' }
            Mock gh { 'gh version 2.70.0 (2025-01-01)' } -ParameterFilter { $args[0] -eq '--version' }
        }

        It 'Fails when gh auth is not configured' {
            Mock gh { $global:LASTEXITCODE = 1 } -ParameterFilter { $args[0] -eq 'auth' -and $args[1] -eq 'status' }
            { & $ScriptPath } | Should -Throw '*not authenticated*gh auth login*'
        }

        It 'Fails when az auth is not configured' {
            Mock gh { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'auth' -and $args[1] -eq 'status' }
            Mock az { $global:LASTEXITCODE = 1 } -ParameterFilter { $args[0] -eq 'account' -and $args[1] -eq 'show' }
            { & $ScriptPath } | Should -Throw '*Azure CLI is not authenticated*az login*'
        }
    }

    Context 'NuGet source configuration' {
        BeforeAll {
            # Use a counter so Get-Command BuildDuty.Mcp returns $null first, then object on second call
            Mock Get-Command {
                $script:mcpCallCount++
                if ($script:mcpCallCount -le 1) { return $null }
                return [PSCustomObject]@{ Name = 'BuildDuty.Mcp' }
            } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'az' } } -ParameterFilter { $Name -eq 'az' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' }
            Mock gh { 'gh version 2.70.0 (2025-01-01)' } -ParameterFilter { $args[0] -eq '--version' }
            Mock gh { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'auth' -and $args[1] -eq 'status' }
            Mock az { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'account' -and $args[1] -eq 'show' }
        }

        BeforeEach {
            $script:mcpCallCount = 0
        }

        It 'Skips adding NuGet source when already configured by name' {
            Mock dotnet {
                @(
                    'Registered Sources:',
                    '  1. nuget.org [Enabled]',
                    '      https://api.nuget.org/v3/index.json',
                    '  2. github-ellahathaway [Enabled]',
                    '      https://nuget.pkg.github.com/ellahathaway/index.json'
                )
            } -ParameterFilter { $args[0] -eq 'nuget' -and $args[1] -eq 'list' }
            Mock dotnet { '' } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'list' }
            Mock dotnet { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }

            # Should NOT call 'gh api user' or 'gh auth token' since source exists
            Mock gh { 'testuser' } -ParameterFilter { $args[0] -eq 'api' }
            & $ScriptPath 2>&1
            Should -Not -Invoke gh -ParameterFilter { $args[0] -eq 'api' }
        }

        It 'Adds NuGet source when not configured' {
            Mock dotnet {
                @(
                    'Registered Sources:',
                    '  1. nuget.org [Enabled]',
                    '      https://api.nuget.org/v3/index.json'
                )
            } -ParameterFilter { $args[0] -eq 'nuget' -and $args[1] -eq 'list' }
            Mock gh { 'testuser' } -ParameterFilter { $args[0] -eq 'api' }
            Mock gh { 'ghp_faketoken123' } -ParameterFilter { $args[0] -eq 'auth' -and $args[1] -eq 'token' }
            Mock dotnet { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'nuget' -and $args[1] -eq 'add' }
            Mock dotnet { '' } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'list' }
            Mock dotnet { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }

            & $ScriptPath 2>&1
            Should -Invoke dotnet -ParameterFilter { $args[0] -eq 'nuget' -and $args[1] -eq 'add' }
        }
    }

    Context 'Tool install vs update' {
        BeforeAll {
            Mock Get-Command {
                $script:mcpCallCount++
                if ($script:mcpCallCount -le 1) { return $null }
                return [PSCustomObject]@{ Name = 'BuildDuty.Mcp' }
            } -ParameterFilter { $Name -eq 'BuildDuty.Mcp' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'gh' } } -ParameterFilter { $Name -eq 'gh' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'az' } } -ParameterFilter { $Name -eq 'az' }
            Mock Get-Command { [PSCustomObject]@{ Name = 'dotnet' } } -ParameterFilter { $Name -eq 'dotnet' }
            Mock gh { 'gh version 2.70.0 (2025-01-01)' } -ParameterFilter { $args[0] -eq '--version' }
            Mock gh { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'auth' -and $args[1] -eq 'status' }
            Mock az { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'account' -and $args[1] -eq 'show' }
            Mock dotnet {
                @(
                    'Registered Sources:',
                    '  1. github-ellahathaway [Enabled]',
                    '      https://nuget.pkg.github.com/ellahathaway/index.json'
                )
            } -ParameterFilter { $args[0] -eq 'nuget' -and $args[1] -eq 'list' }
        }

        BeforeEach {
            $script:mcpCallCount = 0
        }

        It 'Runs dotnet tool install when tool is not yet installed' {
            Mock dotnet { '' } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'list' }
            Mock dotnet { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }
            Mock dotnet {} -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'update' }

            & $ScriptPath 2>&1
            Should -Invoke dotnet -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }
            Should -Not -Invoke dotnet -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'update' }
        }

        It 'Runs dotnet tool update when tool is already installed' {
            Mock dotnet {
                @(
                    'Package Id      Version      Commands',
                    '--------------------------------------------',
                    'ellahathaway.buildduty.mcp      0.0.1      BuildDuty.Mcp'
                )
            } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'list' }
            Mock dotnet { $global:LASTEXITCODE = 0 } -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'update' }
            Mock dotnet {} -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }

            & $ScriptPath 2>&1
            Should -Invoke dotnet -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'update' }
            Should -Not -Invoke dotnet -ParameterFilter { $args[0] -eq 'tool' -and $args[1] -eq 'install' }
        }
    }
}
