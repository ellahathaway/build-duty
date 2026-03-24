# Work Item Lifecycle

## State vs Status

Work items have two distinct fields:

- **`state`** (collection observation) — set by collectors to describe what was observed.
  Values: `"new"`, `"updated"`, `"closed"`, or `"stable"` (processed by triage).
  Triage reads this to decide status changes, then marks the item as `"stable"`.

- **`status`** (triage decision) — set by the triage step to track the item's lifecycle.
  Terminal statuses indicate the item is done.

Collection ONLY sets `state`. Triage reads `state`, makes `status` decisions, then sets `state` to `"stable"`.

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
Other pipeline statuses: `monitoring`, `acknowledged`

**Issue (`github-issue`):** `new` → `needs-investigation` or `monitoring` → `tracked` → `resolved`
Other issue statuses: `acknowledged`

**PR (`github-pr`):** `new` → `needs-review` → `approved` → `needs-merge` → `merged`
Other PR statuses: `changes-requested`, `automerge`, `test-failures`, `merge-conflicts`, `monitoring`, `acknowledged`, `closed`

### Acknowledged vs Monitoring

- **`acknowledged`** — "I've reviewed this and decided no action is needed."
  Acknowledged items are ignored during triage — they will NOT re-triage when
  state changes to `updated`. They only re-enter triage if state becomes `closed`
  (so triage can resolve them) or are manually changed.

- **`monitoring`** — "I'm aware of this and actively watching it."
  Monitoring items WILL re-triage when state changes to `updated`, allowing
  the engineer to stay informed about changes.

### Blocked transitions

- `tracked` → `monitoring` is not allowed. If an item has a linked fix,
  it should stay tracked until resolved.

## ID and Correlation Formats

| Source | ID | Correlation ID |
|--------|----|----------------|
| ADO pipeline | `wi_ado_{buildId}` | `corr_ado_{pipelineId}_{sanitizedBranch}` |
| GitHub issue | `wi_gh_issue_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_issue_{number}` |
| GitHub PR | `wi_gh_pr_{owner}_{repo}_{number}` | `corr_gh_{owner}_{repo}_pr_{number}` |

## Deduplication

Existing work items are provided in the prompt. Skip sources whose ID
already appears in the existing items list.
