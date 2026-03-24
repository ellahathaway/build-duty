# Reference: github-pr source

This reference applies when a work item has a source of type `github-pr`.
The source `ref` is a URL to a GitHub pull request.

## Extracting PR info from URLs

GitHub PR URLs follow this pattern:
- `https://github.com/{owner}/{repo}/pull/{number}`

Extract the **owner**, **repo**, and **PR number** from the URL.

## What to include in the summary

### Basics
- **Title** and **number**
- **State** — open, closed, or merged
- **Author** — who opened it
- **Reviewers** — who's assigned to review; note approval status
- **Labels** — especially priority, area, or status labels
- **Target branch** — where this is merging into

### Review status
- **Approved** / **Changes requested** / **Pending** — current review state
- **Blocking reviewers** — anyone who requested changes or hasn't reviewed
- **Review comments** — summarize unresolved threads

### CI status
- **Check runs** — pass/fail status of CI checks on the PR
- If checks are failing, investigate the build failure details
- Note which checks are required vs optional

### Content
- **Description** — concise summary of what the PR does
- **Changed files** — count and key areas affected
- **Size** — additions/deletions; flag if unusually large

### Cross-references
- **Linked issues** — issues this PR fixes or references
- **Related PRs** — other PRs targeting the same area
- **Correlated work items** — other build-duty items in the same cluster

## Merge readiness

Assess whether the PR is ready to merge:

| Condition | Status |
|-----------|--------|
| All required checks passing | ✅ Ready |
| Has required approvals | ✅ Ready |
| Changes requested | ❌ Blocked |
| Merge conflicts | ❌ Blocked |
| Draft PR | ⏸️ Not ready |
| Missing reviews | ⚠️ Needs attention |

## Suggested next steps

Based on the PR state, suggest appropriate actions:
- **Open + approved + checks green** → merge or set auto-merge
- **Open + changes requested** → address feedback, re-request review
- **Open + failing checks** → investigate CI failures
- **Open + stale (no activity > 3 days)** → ping reviewers
- **Merged + recently** → verify no regressions in post-merge CI
- **Closed without merge** → note why (superseded, abandoned, etc.)
