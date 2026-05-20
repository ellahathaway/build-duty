---
name: reconcile-findings
description: Reconciles pre-analyzed triage findings — grouping related signals, identifying new issues, and determining which previously-known issues have cleared.
---

# Reconcile Findings

Given pre-analyzed triage findings, reconcile them to produce an organized summary of active incidents.

## Input

You receive:
- **Analysis results** — pre-analyzed findings from the analyze-* skills, provided in the prompt
- **Previous findings** (optional) — prior triage results for comparison

## Step 1 — Review Analysis Results

The analysis results contain structured findings from the analyze-* skills. Each finding includes:
- Root cause or summary
- Category (TestFailure, BuildFailure, Timeout, Infrastructure, Dependency, Configuration, Unknown)
- Impact and context
- Evidence (log excerpts, error messages)

Review all findings before proceeding to grouping.

## Step 2 — Group Findings

Group findings that describe the same underlying issue:

- Findings with the same root cause or error signature
- AzDO failures across different branches of the same pipeline (e.g., the same timeout in release/10.0.2xx and release/10.0.3xx)
- GitHub issues and PRs that reference the same failure
- Cross-type matches (e.g., a GitHub issue tracking an AzDO pipeline failure identified by the analysis)

Each group represents a single incident.

Use [references/incident-grouping.md](./references/incident-grouping.md) for detailed grouping criteria.

## Step 3 — Identify New vs Known Issues

For each finding group:
- Is this a new issue not seen in previous findings?
- Is this a recurrence of a previously resolved issue?
- Is this an ongoing issue that was already known?

## Step 4 — Identify Cleared Issues

If previous findings are provided:
- Which previously-active issues have NO matching finding in this run?
- These have likely been resolved.

**Important**: If signal collection had partial failures (some scopes failed), do NOT mark issues as cleared if their scope overlaps with a failed collection scope.

## Output

Produce a structured summary:

- **Active Incidents**: List of grouped findings, each with:
  - `IssueSignature`: A stable fingerprint for matching (e.g., "timeout in stage X of pipeline Y")
  - `Summary`: Concise description
  - `Category`: Failure category
  - `Signals`: List of signal URLs in this group
  - `Status`: `new`, `ongoing`, or `recurrence`
- **Cleared Incidents**: Issues from previous findings that no longer appear
- **Collection Gaps**: Any scopes that failed to collect (signals may be missing)
