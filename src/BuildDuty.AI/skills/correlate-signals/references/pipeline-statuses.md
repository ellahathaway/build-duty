# Pipeline Failure Statuses

Signal type: `ado-pipeline-run`

## Valid statuses

| Status | Meaning |
|--------|---------|
| `new` | Just created, not yet reviewed |
| `tracked` | Acknowledged, being monitored |
| `investigating` | Actively investigating root cause |
| `fixed` | Issue resolved (pipeline passing again) |

## Status determination

Use the build result from the signal title or the `az` CLI to determine status:

| Build result | Work item status |
|-------------|-----------------|
| `failed` (first occurrence) | `tracked` |
| `failed` (recurring, same correlation ID) | `investigating` |
| `succeeded` (after prior failure) | `fixed` |
| `canceled` | `tracked` |
| `partiallySucceeded` | `tracked` |

## Efficiency rules

1. **Group first** — Group work items by `correlationId`. Items with the same
   correlation ID share the same pipeline+branch, so only investigate the
   **most recent** build per group. Apply findings to all items in the group.
2. **Write as you go** — Call `update_work_item_status` after each group.
   Do NOT wait until you've analyzed all builds.

## Cross-referencing

- If a PR exists on the same branch, link the pipeline failure to the PR work item.
- If a GitHub issue mentions the pipeline or failure, link them.
