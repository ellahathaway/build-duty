---
name: analyze-github-pull-request
description: Analyzes GitHub pull request to understand proposed changes, merge/CI status, and relationship to tracked issues using GitHub MCP tools.
---

# Analyze GitHub Pull Request

Investigate GitHub pull request to understand its status and relationship to tracked issues

## Prerequisites
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"

## Context

The information given likely contains the following fields:
- `Url` — link to the PR
- `Organization` — GitHub org (e.g., `dotnet`)
- `Repository` — repo name (e.g., `runtime`)
- `Merged` — whether the PR has been merged
- `Item` — PR metadata:
  - `Number` — PR number
  - `Title` — PR title
  - `State` — current state (`open`, `closed`)
  - `UpdatedAt` — last update timestamp
  - `Labels` — list of label names
- `Checks` — CI check results with `Name`, `Status`, and `Conclusion`

## Investigate PR

### 1. Understand the change

Use the `github-mcp-server` MCP tools to read the PR body and understand:
- What is this PR trying to fix or change?
- Does it reference any issues (e.g., "Fixes #1234")?
- Is this a fix for a build/test failure, a dependency update, or a feature change?

Do NOT use non-MCP tools for querying GitHub — always use MCP tools.

### 2. Check merge and review status

- Is the PR `open`, `closed`, or `Merged`?
- If open: is it in draft? Are there review comments or requested changes?
- If merged: when was it merged?

### 3. Analyze CI checks

Review `Checks` to understand CI health:
- Are all checks passing (`Conclusion` = `success`)?
- Which checks are failing and what are they?
- Is the PR blocked by CI failures?

### 4. Identify related issues

Use `github-mcp-server` MCP tools to find:
- Which issues does this PR fix or reference?
- Are there cross-references to other PRs?
- Is this PR part of a larger effort (e.g., backports across branches)?

## Output

Produce:
- **Summary**: A concise description of what the PR does
- **Status**: Current state (e.g., "Open, CI passing, awaiting review", "Merged 2 days ago", "Open but blocked by failing checks")
- **CIHealth**: Summary of check results — all passing, some failing, or not yet run
- **LinkedIssues**: Issues this PR fixes or references, and their current state
- **Impact**: What the PR changes affect — which components, pipelines, or behaviors
- **Context**: Any additional context — is this a backport? Is it urgent? Are there review concerns?
