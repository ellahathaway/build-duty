# Work Item Lifecycle

## Status

Work items have a single `status` field (string) that tracks their lifecycle.
Terminal statuses indicate the item is done.

### Terminal statuses (resolved)

| Status | Meaning |
|--------|---------|
| `resolved` | Generic resolution |
| `fixed` | Pipeline/build issue fixed |
| `merged` | PR merged |
| `closed` | Issue or PR closed |

### Active statuses (by source type)

**Pipeline (`ado-pipeline-run`):** `new` → `needs-investigation` → `tracked` (when linked to issue/PR) → `fixed`
Other pipeline statuses: `investigating`

**Issue (`github-issue`):** `new` → `monitoring` → `in-pr` → `resolved`

**PR (`github-pr`):** `new` → `needs-review` → `approved` → `needs-merge` → `merged`
Other PR statuses: `changes-requested`, `automerge`, `test-failures`, `merge-conflicts`, `closed`

## ID and Correlation Formats

| Source | ID | Correlation ID |
|--------|----|----------------|
| ADO pipeline | `wi_ado_{buildId}` | `corr_ado_{pipelineId}_{sanitizedBranch}` |
| GitHub issue | `wi_gh_issue_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_issue_{number}` |
| GitHub PR | `wi_gh_pr_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_pr_{number}` |

## Deduplication

Existing work items are provided in the prompt. Skip sources whose ID
already appears in the existing items list.
