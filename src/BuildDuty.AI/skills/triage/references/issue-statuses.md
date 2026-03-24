# GitHub Issue Statuses

Source type: `github-issue`

## Valid statuses

| Status | Meaning |
|--------|---------|
| `new` | Just created, not yet reviewed |
| `monitoring` | Watching for updates |
| `in-pr` | Has an associated pull request |
| `resolved` | Issue closed |

## Status determination

| Issue state | Linked PR? | Work item status |
|------------|-----------|-----------------|
| `open` + no PR | `monitoring` |
| `open` + linked PR | `in-pr` |
| `closed` | — | `resolved` |

## Cross-referencing

- If a PR title or body references this issue, link them.
- If a pipeline failure is related to the issue's topic, link them.
