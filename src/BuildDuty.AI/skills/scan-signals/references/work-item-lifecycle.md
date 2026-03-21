# Work Item Lifecycle

## States

| State | Meaning |
|-------|---------|
| `unresolved` | New failure or issue, needs attention |
| `inprogress` | Being investigated |
| `resolved` | No longer active (build fixed, issue closed, etc.) |

Transitions: `unresolved → inprogress → resolved`

Auto-resolution goes through `inprogress` automatically.

## ID and Correlation Formats

| Source | ID | Correlation ID |
|--------|----|----------------|
| ADO pipeline | `wi_ado_{buildId}` | `corr_ado_{pipelineId}_{sanitizedBranch}` |
| GitHub issue | `wi_gh_issue_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_issue_{number}` |
| GitHub PR | `wi_gh_pr_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_pr_{number}` |

## Deduplication

Existing work items are provided in the prompt. Skip signals whose ID
already appears in the existing items list.
