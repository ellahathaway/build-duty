---
name: triage
description: Full build-duty triage workflow — collects signals from configured sources, analyzes each failure, and reconciles findings into an organized incident report.
---

# Triage

Run a full build-duty triage cycle: collect signals, analyze each one, and reconcile findings.

## Prerequisites

### MCP Servers
- The Build-Duty MCP server must be available
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

### Config Files

A config file is a `.yml` file that specifies:
- Which Azure DevOps pipelines to monitor (by org/project or specific pipeline IDs)
- Which GitHub repos to monitor for issues and PRs (by org/repo or specific filters)

A config path must be provided as input when triage starts.

If a config path is not provided, prompt the user for the path to a `.build-duty.yml` file before proceeding.

## Step 1 — Collect Signals

Use the `build_duty_collect_signals` tool with a required `configPath` value to collect signals from the configured sources.

This returns structured signal data including:
- Azure DevOps pipeline failures (with timeline records and log IDs)
- GitHub issues matching configured filters
- GitHub PRs matching configured filters
- Collection coverage (which scopes succeeded/failed)

If any collection step fails, alert the user. Do not proceed to analysis until the user confirms they want to continue, abort, or retry.

## Step 2 — Analyze Each Signal

For each collected signal, invoke the appropriate analysis skill:

- **AzureDevOpsPipeline** signals → invoke `/analyze-azure-devops-pipeline`
- **GitHubIssue** signals → invoke `/analyze-github-issue`
- **GitHubPullRequest** signals → invoke `/analyze-github-pull-request`

Pass the signal data to the skill as context. Each skill will investigate and produce structured findings (RootCause, Category, Context, etc.).

## Step 3 — Reconcile Findings

Once all signals are analyzed, invoke `/reconcile-findings` with:
- All analysis results from Step 2
- Previous findings (if available from a prior triage run)

This groups related findings into incidents and identifies new vs ongoing vs cleared issues.

## Step 4 — Present Summary

Present the user with a structured triage report. The report header should include the run date, total signal count (broken down by type), and total finding count.

Every signal must be accounted for in exactly one finding. At the end of the report, include a **Signal Coverage Summary** that maps signal counts to findings so the user can verify nothing was dropped.

### New Incidents

For each newly-discovered incident:

1. **Title** — short descriptive name
2. **Root Cause** — concise explanation of why the failure occurs
3. **Category** — one of: TestFailure, BuildFailure, Timeout, Infrastructure, Dependency, Configuration, Unknown
4. **Signals table** — a table listing every signal grouped into this incident:

| Type | Signal | Branch | Detail |
|------|--------|--------|--------|
| AzDo Pipeline | [Build NNNNNN](url) | branch | what failed in this build |
| GitHub Issue | [#NNN](url) | — | issue title and status |
| GitHub PR | [#NNN](url) | branch | PR title and status |

5. **Additional context** — any relevant notes: how long this has been failing, workarounds, who to contact, linked issues, etc.
6. **Suggested next steps** — actionable recommendations for this incident. Examples:
   - "Needs issue" — no tracking issue exists; suggest filing one
   - "PR open" — a fix PR exists; link it and note its merge status
   - "Assigned, awaiting fix" — an issue is assigned but no PR yet
   - "Needs investigation" — root cause unclear; suggest deeper log analysis
   - "Retry/re-run" — transient infra failure; suggest retrying the build
   - "Credential rotation needed" — auth failure; suggest contacting the team that owns the PAT/secret
   - "Baseline update needed" — test diff requires updating a baseline file
   - "Timeout increase or optimization needed" — build exceeds time limit

### Ongoing Incidents

Same structure as New Incidents, but for issues that were previously known and are still active. Include:
- Link to the tracking issue (if one exists)
- Assignee (if any)
- How long this has been ongoing
- Suggested next steps (e.g., "ping assignee", "escalate — stale for 4+ weeks", "check if fix PR has merged")

### Cleared Incidents

List issues that were previously tracked but are no longer detected (likely resolved). Note what resolved them if known.

### Collection Gaps

Note any scopes that failed to collect — findings may be incomplete.

### Tracked Issues (Low Activity)

List any GitHub issues that matched the configured filters but have had no recent activity (>30 days since last update). These may need attention or label cleanup.

## Step 5 — Offer Automated Next Steps

After presenting the report, prompt the user to select which next steps to execute automatically. Present a numbered list of all actionable next steps across all incidents, grouped by action type:

**File issues:**
- [ ] Incident N: File a GitHub issue in dotnet/source-build for "..."

**Ping/escalate:**
- [ ] Incident N: Comment on #NNNN asking for status update (stale X weeks)

**Retry builds:**
- [ ] Incident N: Re-run build NNNNNN (transient infra failure)

**Check fix status:**
- [ ] Incident N: Check if PR #NNN has merged and flowed

**Investigate further:**
- [ ] Incident N: Run deeper log analysis to investigate root cause (unclear failure)

Ask the user: "Which of these would you like me to do? (all / none / comma-separated numbers)"

Then execute the selected actions using the appropriate tools (GitHub MCP for issues/comments, AzDo MCP for build retries, etc.).

## Output

A structured triage report followed by an interactive prompt for automated follow-up actions.

## JSON Output Mode

When the caller requests **JSON output** (e.g., "output as JSON", "return JSON"), skip the markdown report and Step 5 interactive prompt. Instead, output a single fenced JSON code block with this schema:

```json
{
  "signals": [
    {
      "type": "azdo_build | github_issue | github_pr",
      "title": "Short descriptive name",
      "buildId": "12345",
      "branch": "main",
      "status": "failed | partially_succeeded | open | closed",
      "url": "https://dev.azure.com/...",
      "pipeline": "dotnet-dotnet (1330)",
      "number": 123,
      "repo": "dotnet/source-build"
    }
  ],
  "incidents": [
    {
      "title": "Short incident name",
      "severity": "high | medium | low",
      "category": "TestFailure | BuildFailure | Timeout | Infrastructure | Dependency | Configuration | Unknown",
      "description": "1-2 sentence summary",
      "rootCause": "Concise explanation",
      "affectedBranches": ["main", "release/10.0.4xx"],
      "signals": [ ... ],
      "nextSteps": "Actionable recommendation"
    }
  ]
}
```

### Field rules:
- **type**: `azdo_build` for pipelines, `github_issue` for issues, `github_pr` for PRs.
- **buildId**: Numeric AzDO build ID as a string. Only for `azdo_build`.
- **pipeline**: Format as `"name (definitionId)"`. Only for `azdo_build`.
- **number** / **repo**: Only for `github_issue` and `github_pr`.
- **incidents[].signals**: The subset of top-level signals grouped into this incident. Same schema as top-level signals.
- **severity**: `high` = active regressions or blocking; `medium` = ongoing tracked; `low` = warnings or stale.
