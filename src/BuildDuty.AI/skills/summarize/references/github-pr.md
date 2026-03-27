# Reference: GitHub Pull Request

## When to use

Use this when the signal type is a GitHub pull request signal (`github-pr`).

## Signal info

Use stored signal payload only (metadata, review status, checks, and state).
Do not fetch external data during summarization.

## Summary focus

- PR title/number and state (open/merged/closed)
- Merge readiness (approvals, requested changes, draft/conflicts)
- CI/check status, especially failing required checks
- High-level change intent and unresolved review blockers

## Keep it concise
- 1 sentence preferred, max 2.
- Focus on blocked vs ready and the top blocker.
- No markdown or bullet formatting in final summary text.
