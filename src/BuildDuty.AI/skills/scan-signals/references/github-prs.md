# GitHub PR Signals

Signal type: `github-pr`. Collected deterministically — the AI does not query GitHub.

PRs are matched by **name patterns** configured per repository. Patterns prefixed with `*` match as substring (contains); otherwise exact match (case-insensitive). Each pattern can specify a `state` (default: `open`).

## Signal fields

| Field | Format |
|-------|--------|
| ID | `wi_gh_pr_{owner}_{repo}_{prNumber}` |
| Title | `[{owner}/{repo}#{prNumber}] {prTitle}` |
| Correlation ID | `corr_gh_{owner}_{repo}_pr_{prNumber}` |
| Signal type | `github-pr` |
| Signal ref | `https://github.com/{owner}/{repo}/pull/{number}` |
| Status | `open`, `closed`, or `merged` |

## Triage rules

- Open PR + no existing work item → `create_work_item`
- Existing work item + PR now `merged` → `resolve_work_item` with `"Auto-resolved: PR merged"`
- Existing work item + PR now `closed` → `resolve_work_item` with `"Auto-resolved: PR closed"`
