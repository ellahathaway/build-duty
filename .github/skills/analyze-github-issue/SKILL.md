---
name: analyze-github-issue
description: Analyzes a GitHub issue to understand tracked problems, current status, and linked fixes.
---

# Analyze GitHub Issue

Investigate a GitHub issue to understand what is being tracked and its current state. Issue data is provided in the prompt.

## Prerequisites
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"

## Context

The information given likely contains the following fields:
- `Url` — link to the issue
- `Organization` — GitHub org (e.g., `dotnet`)
- `Repository` — repo name (e.g., `source-build`)
- `Item` — issue metadata:
  - `Number` — issue number
  - `Title` — issue title
  - `State` — current state (`open`, `closed`)
  - `UpdatedAt` — last update timestamp
  - `Labels` — list of label names

## Investigate Issue

### 1. Understand the issue

Use the `github-mcp-server` MCP tools to read the issue body and understand:
- What problem is being reported?
- Is this a build failure, test failure, infrastructure issue, feature request, or tracking issue?
- Does the body contain error messages, stack traces, or reproduction steps?

Do NOT use non-MCP tools for querying GitHub — always use MCP tools.

### 2. Check current status and activity

- Is the issue `open` or `closed`?
- Use MCP tools to read recent comments — is someone actively working on this? Has a root cause been identified?
- Check for linked PRs — are there PRs that fix this issue? Are they merged, open, or closed?

### 3. Identify linked fixes

Look for PRs that reference this issue and check their status.

## Output

Produce:
- **Summary**: A concise description of what the issue is tracking
- **Status**: Current state and activity level (e.g., "Open, actively being investigated", "Open but stale — no activity in 30 days", "Closed, fixed by PR #123")
- **LinkedFixes**: Any PRs or commits that address this issue, and their status
- **Impact**: What systems, pipelines, or repos are affected by this issue
- **Context**: Any additional context — workarounds, related issues, labels, milestones
