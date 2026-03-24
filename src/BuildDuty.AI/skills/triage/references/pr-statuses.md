# GitHub PR Statuses

Source type: `github-pr`

## Valid statuses

| Status | Meaning |
|--------|---------|
| `new` | Just created, not yet reviewed |
| `needs-review` | Awaiting reviewer feedback |
| `changes-requested` | Reviewer requested changes |
| `approved` | Approved by reviewers |
| `needs-merge` | Approved but not yet merged |
| `automerge` | Auto-merge is enabled, waiting on checks |
| `test-failures` | CI checks are failing |
| `merge-conflicts` | Has merge conflicts |
| `monitoring` | Watching for updates — will re-triage when source changes |
| `acknowledged` | Reviewed — no action needed, ignore unless resolved |
| `merged` | PR has been merged |
| `closed` | Closed without merging |

## Status determination

Use the PR's `reviewDecision`, `statusCheckRollup`, `mergeable`, and `autoMergeRequest` fields:

| Condition | Status |
|-----------|--------|
| `state: MERGED` | `merged` |
| `state: CLOSED` | `closed` |
| `autoMergeRequest` present | `automerge` |
| `mergeable: CONFLICTING` | `merge-conflicts` |
| Any check `FAILURE` | `test-failures` |
| `reviewDecision: APPROVED` + checks passing | `needs-merge` |
| `reviewDecision: APPROVED` | `approved` |
| `reviewDecision: CHANGES_REQUESTED` | `changes-requested` |
| `reviewDecision: REVIEW_REQUIRED` | `needs-review` |

Evaluate conditions top-to-bottom; first match wins.

## Cross-referencing

- If a pipeline failure exists on the same branch as this PR, link them.
- If an issue is referenced in the PR body, link them.
