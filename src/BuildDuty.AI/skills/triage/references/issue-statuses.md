# GitHub Issue Statuses

Source type: `github-issue`

## Valid statuses

| Status | Meaning |
|--------|---------|
| `new` | Just created, not yet reviewed |
| `needs-investigation` | Needs attention — no linked PR addressing it |
| `monitoring` | Watching for updates |
| `acknowledged` | Reviewed — no action needed, ignore unless resolved |
| `tracked` | Has a linked item (issue or PR) addressing it |
| `resolved` | Issue closed |

## Status determination

| Issue state | Linked PR? | Work item status |
|------------|-----------|-----------------|
| `open` + no PR | `needs-investigation` or `monitoring` |
| `open` + linked PR | `tracked` |
| `closed` | — | `resolved` |

## Cross-referencing

- If a PR title or body references this issue, link them.
- If a pipeline failure is related to the issue's topic, link them.
