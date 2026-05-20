---
name: review-work-items
description: Interactively review and investigate active triage findings — dig deeper into root causes, check for fixes, and determine next actions.
---

# Review Work Items

Help the user investigate and review active triage findings. This skill is used when the user wants to understand a specific failure in more detail or determine what action to take.

## Prerequisites
- The Build-Duty MCP server must be available
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

## Input

You receive one or more triage findings (from the reconcile-findings skill output or from collected signal data) that the user wants to investigate further.

## Investigation

For each finding the user wants to review:

### 1. Gather additional context

- If it's an **AzDO pipeline failure**: Use the `azure-devops-mcp-server` MCP tools to fetch additional log details, check if the pipeline has passed since, or look at recent build history.
- If it's a **GitHub issue/PR**: Use the `github-mcp-server` MCP tools to read comments, check for linked PRs, see if a fix has been merged.

### 2. Determine current state

- Is this issue still actively failing? (Check recent builds/runs)
- Has someone already filed a fix?
- Is there a known workaround?
- Is this a flaky test vs a persistent failure?

### 3. Suggest next actions

Based on the investigation, suggest:
- **Investigate further** — need more data (specify what)
- **Wait for fix** — a PR is open/merged, monitor for resolution
- **File an issue** — no existing tracking, provide suggested title and labels
- **Retry/rerun** — likely transient (infrastructure issue, flaky test)
- **Resolved** — the issue has cleared since the last triage

## Output

For each reviewed finding, produce:
- **Current Status**: Is it still failing? Fixed? In progress?
- **Root Cause** (if determinable): What's causing this?
- **Recommended Action**: What should the user do next?
- **Evidence**: Links, log excerpts, or references supporting the recommendation
