#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

<#
.SYNOPSIS
    Behavioral tests for eng/plugin-scripts/comment-review-gate.ps1 (and .sh).
.DESCRIPTION
    Pipes representative preToolUse payloads to the gate hook and asserts that
    GitHub issue/PR comment and review posts are escalated to a "ask" decision,
    while reads and unrelated tool calls produce no output. Also verifies the
    BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS opt-out. The gate must always exit 0.
#>

BeforeAll {
    $RepoRoot = Join-Path $PSScriptRoot '..' '..' '..'
    $GatePs1 = (Resolve-Path (Join-Path $RepoRoot 'eng' 'plugin-scripts' 'comment-review-gate.ps1')).Path
    $GateSh = Join-Path $RepoRoot 'eng' 'plugin-scripts' 'comment-review-gate.sh'
    if (Test-Path $GateSh) { $GateSh = (Resolve-Path $GateSh).Path }
    # bash on Windows accepts forward-slash paths (C:/Users/...) but not backslashes.
    $GateShForBash = $GateSh -replace '\\', '/'

    # Invoke the PowerShell gate in a child process so stdin is captured by
    # [Console]::In.ReadToEnd(). Returns the trimmed stdout.
    function Invoke-GatePs1 {
        param([string]$Payload, [hashtable]$EnvVars = @{})
        $previous = @{}
        foreach ($k in $EnvVars.Keys) {
            $previous[$k] = [Environment]::GetEnvironmentVariable($k)
            Set-Item "Env:$k" $EnvVars[$k]
        }
        try {
            $out = $Payload | & pwsh -NoProfile -File $GatePs1
            return (($out -join "`n").Trim())
        }
        finally {
            foreach ($k in $EnvVars.Keys) {
                if ($null -eq $previous[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
                else { Set-Item "Env:$k" $previous[$k] }
            }
        }
    }
}

Describe 'comment-review-gate.ps1' {

    Context 'GitHub comment/review posts are escalated to ask' {
        It 'asks for MCP <tool>' -ForEach @(
            @{ tool = 'add_issue_comment' }
            @{ tool = 'add_comment_to_pending_review' }
            @{ tool = 'create_and_submit_pull_request_review' }
            @{ tool = 'submit_pending_pull_request_review' }
            @{ tool = 'create_pull_request_review' }
            @{ tool = 'add_pull_request_review_comment' }
        ) {
            $payload = '{"toolName":"github-mcp-server-' + $tool + '","toolArgs":{"body":"hello"}}'
            $out = Invoke-GatePs1 -Payload $payload
            $out | Should -Match '"permissionDecision":"ask"'
        }

        It 'asks for shell <desc>' -ForEach @(
            @{ desc = 'gh issue comment'; cmd = 'gh issue comment 73 --body-file body.md' }
            @{ desc = 'gh pr comment';    cmd = 'gh pr comment 5 --body "hi"' }
            @{ desc = 'gh pr review';      cmd = 'gh pr review 5 --comment --body "lgtm"' }
            @{ desc = 'gh api comments';   cmd = 'gh api repos/o/r/issues/1/comments -f body=hi' }
            @{ desc = 'gh api reviews';    cmd = 'gh api repos/o/r/pulls/1/reviews -f body=hi -f event=COMMENT' }
        ) {
            $payload = '{"toolName":"powershell","toolArgs":{"command":"' + $cmd.Replace('"', '\"') + '"}}'
            $out = Invoke-GatePs1 -Payload $payload
            $out | Should -Match '"permissionDecision":"ask"'
        }

        It 'emits valid JSON with a reason when asking' {
            $out = Invoke-GatePs1 -Payload '{"toolName":"add_issue_comment","toolArgs":{"body":"x"}}'
            $obj = $out | ConvertFrom-Json
            $obj.permissionDecision | Should -Be 'ask'
            $obj.permissionDecisionReason | Should -Not -BeNullOrEmpty
        }
    }

    Context 'Reads and unrelated tool calls are not gated' {
        It 'stays silent for <desc>' -ForEach @(
            @{ desc = 'gh pr view --comments';  cmd = 'gh pr view 5 --comments' }
            @{ desc = 'gh issue view';          cmd = 'gh issue view 73' }
            @{ desc = 'gh api GET comments';    cmd = 'gh api repos/o/r/issues/1/comments' }
            @{ desc = 'dotnet build';           cmd = 'dotnet build BuildDuty.slnx' }
            @{ desc = 'git status';             cmd = 'git status' }
        ) {
            $payload = '{"toolName":"powershell","toolArgs":{"command":"' + $cmd.Replace('"', '\"') + '"}}'
            $out = Invoke-GatePs1 -Payload $payload
            $out | Should -BeNullOrEmpty
        }

        It 'stays silent for an empty payload' {
            $out = Invoke-GatePs1 -Payload ''
            $out | Should -BeNullOrEmpty
        }
    }

    Context 'Opt-out via BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS' {
        It 'stays silent for a comment post when opt-out is <value>' -ForEach @(
            @{ value = '1' }
            @{ value = 'true' }
            @{ value = 'yes' }
            @{ value = 'on' }
        ) {
            $payload = '{"toolName":"add_issue_comment","toolArgs":{"body":"x"}}'
            $out = Invoke-GatePs1 -Payload $payload -EnvVars @{ BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS = $value }
            $out | Should -BeNullOrEmpty
        }

        It 'still asks when opt-out is set to a non-truthy value' {
            $payload = '{"toolName":"add_issue_comment","toolArgs":{"body":"x"}}'
            $out = Invoke-GatePs1 -Payload $payload -EnvVars @{ BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS = '0' }
            $out | Should -Match '"permissionDecision":"ask"'
        }
    }

    Context 'Bash gate parity (when bash is available)' {
        BeforeAll {
            $bash = Get-Command bash -ErrorAction SilentlyContinue
            # Resolve a path the *resolved* bash flavor can actually read. git-bash
            # understands C:/... forward-slash paths; WSL bash needs /mnt/c/...
            $BashScriptPath = $null
            if ($bash) {
                $candidates = @($GateShForBash)
                if ($GateShForBash -match '^([A-Za-z]):/(.*)$') {
                    $candidates += ('/mnt/' + $matches[1].ToLower() + '/' + $matches[2])
                }
                foreach ($candidate in $candidates) {
                    $check = & bash -c "test -f '$candidate' && echo ok" 2>$null
                    if ($check -eq 'ok') { $BashScriptPath = $candidate; break }
                }
            }
        }

        It 'bash gate asks for a comment post' {
            if (-not $bash) { Set-ItResult -Skipped -Because 'bash is not available'; return }
            if (-not $BashScriptPath) { Set-ItResult -Skipped -Because 'bash cannot access the script path in this environment'; return }
            $payload = '{"toolName":"add_issue_comment","toolArgs":{"body":"x"}}'
            $out = ($payload | & bash $BashScriptPath) -join "`n"
            $out | Should -Match '"permissionDecision":"ask"'
        }

        It 'bash gate stays silent for a benign command' {
            if (-not $bash) { Set-ItResult -Skipped -Because 'bash is not available'; return }
            if (-not $BashScriptPath) { Set-ItResult -Skipped -Because 'bash cannot access the script path in this environment'; return }
            $payload = '{"toolName":"powershell","toolArgs":{"command":"dotnet build"}}'
            $out = ($payload | & bash $BashScriptPath) -join "`n"
            $out.Trim() | Should -BeNullOrEmpty
        }
    }
}
