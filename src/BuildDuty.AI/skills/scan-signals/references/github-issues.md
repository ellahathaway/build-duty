# GitHub Issue Signals

Signal type: `github-issue`. Collected deterministically — the AI does not query GitHub.

## Signal fields

| Field | Format |
|-------|--------|
| ID | `wi_gh_issue_{owner}_{repo}_{issueNumber}` |
| Title | `[{owner}/{repo}#{issueNumber}] {issueTitle}` |
| Correlation ID | `corr_gh_{owner}_{repo}_issue_{issueNumber}` |
| Signal type | `github-issue` |
| Signal ref | `https://github.com/{owner}/{repo}/issues/{number}` |

## Triage rules

- Open issue + no existing work item → `create_work_item`
- Existing work item + issue now `closed` → `resolve_work_item` with `"Auto-resolved: issue closed"`
