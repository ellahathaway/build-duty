<#
.SYNOPSIS
    build-duty preToolUse gate: review GitHub comments before they are posted (issue #73).
.DESCRIPTION
    Runs as a Copilot CLI 'preToolUse' command hook. It reads the tool-call payload
    from stdin and, when the call would post a comment or review on a GitHub issue or
    pull request, returns a JSON decision of "ask" so the agent must surface the
    rendered comment for confirmation first -- even under --yolo / autopilot. This is
    the deterministic backstop for the 'post-comment' skill, which renders and verifies
    the comment text.

    The hook NEVER denies and always exits 0: a bug here can only ever escalate a
    comment post to a human "ask", never block an unrelated tool call or the session.

    Reviewers who deliberately want to post without the review gate can opt out by
    setting the environment variable BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS=1.
#>

try {
    $optOut = $env:BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS
    if ($optOut -and ($optOut -match '^(1|true|yes|on)$')) {
        exit 0
    }

    $payload = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($payload)) {
        exit 0
    }

    $isComment = $false

    # GitHub MCP server tools that create issue/PR comments or reviews.
    $mcpTools = @(
        'add_issue_comment',
        'add_comment_to_pending_review',
        'create_and_submit_pull_request_review',
        'submit_pending_pull_request_review',
        'create_pull_request_review',
        'add_pull_request_review_comment'
    )
    foreach ($tool in $mcpTools) {
        if ($payload -like "*$tool*") { $isComment = $true; break }
    }

    # gh CLI invoked through the shell tool.
    if (-not $isComment) {
        if ($payload -match 'gh\s+issue\s+comment' -or
            $payload -match 'gh\s+pr\s+comment' -or
            $payload -match 'gh\s+pr\s+review') {
            $isComment = $true
        }
        elseif ($payload -match 'gh\s+api' -and
                $payload -match '/(comments|reviews)' -and
                $payload -match 'body') {
            $isComment = $true
        }
    }

    if (-not $isComment) {
        exit 0
    }

    $reason = 'build-duty: review the rendered comment before posting (issue #73). ' +
              'Confirm the scope and fix/PR links are correct and that the text matches the previewed comment. ' +
              'Set BUILD_DUTY_ALLOW_UNREVIEWED_COMMENTS=1 to opt out.'
    $decision = [ordered]@{
        permissionDecision       = 'ask'
        permissionDecisionReason = $reason
    } | ConvertTo-Json -Compress
    Write-Output $decision
    exit 0
}
catch {
    # Fail open: never let a gate error block tool execution.
    exit 0
}
