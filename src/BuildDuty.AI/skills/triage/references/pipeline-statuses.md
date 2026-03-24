# Pipeline Failure Statuses

Source type: `ado-pipeline-run`

## Valid statuses

| Status | Meaning |
|--------|---------|
| `new` | Just created, not yet reviewed |
| `needs-investigation` | Failure with no linked issue or PR — needs attention |
| `tracked` | Has a linked issue or PR addressing the failure |
| `investigating` | Actively investigating root cause |
| `acknowledged` | Reviewed and accepted — no further action needed right now |
| `fixed` | Issue resolved (pipeline passing again) |

## Status determination

A pipeline failure is only `tracked` when there is a linked work item (issue or PR)
that addresses it. Without a link, it stays `needs-investigation`.

| Build result | Has linked issue/PR? | Work item status |
|-------------|---------------------|-----------------|
| `failed` | No | `needs-investigation` |
| `failed` | Yes | `tracked` |
| `failed` (recurring, same correlation ID) | No | `investigating` |
| `failed` (recurring, same correlation ID) | Yes | `tracked` |
| `succeeded` (after prior failure) | — | `fixed` |
| `canceled` | No | `needs-investigation` |
| `canceled` | Yes | `tracked` |
| `partiallySucceeded` | No | `needs-investigation` |
| `partiallySucceeded` | Yes | `tracked` |

## Efficiency rules

1. **Group first** — Group work items by `correlationId`. Items with the same
   correlation ID share the same pipeline+branch, so only investigate the
   **most recent** build per group. Apply findings to all items in the group.
2. **Write as you go** — Call `update_work_item_status` after each group.
   Do NOT wait until you've analyzed all builds.
3. **Check existing links** — Use the `linkedItems` data provided in the
   prompt to decide between `needs-investigation` and `tracked`.

## Cross-referencing

- If a PR exists on the same branch, link the pipeline failure to the PR work item.
- If a GitHub issue mentions the pipeline or failure, link them.
- After linking, update the pipeline failure status to `tracked`.
