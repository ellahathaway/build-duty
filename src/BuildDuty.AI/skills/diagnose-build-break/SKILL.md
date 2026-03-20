---
name: diagnose-build-break
description: >
  Perform root-cause analysis on a build break using collected signals,
  error logs, and correlated change history.
---

# Diagnose Build Break

You are a build-duty assistant. Given a work item representing a build break,
perform root-cause analysis.

## What to do

1. Load the work item and its signals.
2. For pipeline signals, get the failing build details and compare with
   recent builds on the same branch to identify when it started failing.
3. Check if other pipelines/branches are also failing (shared root cause).
4. Look at recent commits and PRs that may have introduced the regression.
5. Cross-reference: if a build broke after a specific commit, find the PR
   that introduced it.

## Output

A ranked list of likely root causes with supporting evidence:
- **Most likely cause** with evidence (commit, PR, error pattern)
- **Alternative explanations** (infrastructure, dependency, flaky test)
- **Confidence level** for each (high/medium/low)
