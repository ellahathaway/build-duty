# Reference: github-issue signal

This reference applies when a work item has a signal of type `github-issue`.
The signal `ref` is a URL to a GitHub issue.

## Extracting issue info from URLs

GitHub issue URLs follow this pattern:
- `https://github.com/{owner}/{repo}/issues/{number}`

Extract the **owner**, **repo**, and **issue number** from the URL.

## What to include in the summary

### Basics
- **Title** and **number**
- **State** — open or closed
- **Assignees** — who is responsible; flag if unassigned
- **Labels** — especially priority, area, or triage labels
- **Milestone** — if set, note the target date

### Activity
- **Created** — when and by whom
- **Last updated** — how recently; flag if stale (>7 days with no activity)
- **Comment count** — is there active discussion?
- **Latest comments** — summarize the most recent 2-3 comments for context

### Content
- **Description** — concise summary of the issue body
- **Reproduction steps** — if present, note them
- **Error messages** — quote key errors verbatim

### Cross-references
- **Linked PRs** — any pull requests that reference or fix this issue
- **Linked builds** — if the issue references a build failure, include
  the build result/status
- **Related issues** — mentioned or cross-referenced issues
- **Correlated work items** — if this build-duty work item has a correlation
  ID, note other work items in the same cluster

## Priority signals

| Signal | Meaning |
|--------|---------|
| `priority/0` or `P0` label | Critical — needs immediate attention |
| `blocking` label | Blocking other work |
| `untriaged` label | Needs triage assignment |
| Unassigned + open > 24h | May be falling through the cracks |
| No activity > 7 days | Potentially stale |

## Suggested next steps

Based on the issue state, suggest appropriate actions:
- **Open + unassigned** → assign to on-call or appropriate team
- **Open + stale** → ping assignee or escalate
- **Open + has fix PR** → check PR status, nudge review if needed
- **Closed + recently** → verify the fix landed and no regressions
