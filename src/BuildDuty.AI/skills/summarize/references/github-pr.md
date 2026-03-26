# Reference: GitHub Pull Request

## When to use

Use this when the signal type is a GitHub pull request signal (`github-pr`).

## Signal info

Signal info includes PR metadata, description, review status, checks, and state.
Use signal info as the primary source of truth.

## Identify the signal

- URL format:
	- `https://github.com/{owner}/{repo}/pull/{number}`

Extract `owner`, `repo`, and `number` from the URL.

## Additional lookup

Only fetch additional data if required context is missing from signal info.

## Summary focus

- PR title/number and state (open/merged/closed)
- Merge readiness (approvals, requested changes, draft/conflicts)
- CI/check status, especially failing required checks
- High-level change intent and unresolved review blockers

## Output

Return 1-3 sentences focused on whether the PR is blocked or ready.

Do not include extra sections, bullets, or markdown tables in the final summary.
