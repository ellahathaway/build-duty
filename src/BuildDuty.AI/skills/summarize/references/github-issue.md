# Reference: GitHub Issue

## When to use

Use this when the signal type is a GitHub issue signal (`github-issue`).

## Signal info

Signal info includes issue metadata, description, comments, and state.
Use signal info as the primary source of truth.

## Identify the signal

- URL format:
	- `https://github.com/{owner}/{repo}/issues/{number}`

Extract `owner`, `repo`, and `number` from the URL.

## Additional lookup

Only fetch additional data if required context is missing from signal info.

## Summary focus

- Issue title/number and state (open/closed)
- Most relevant problem details from body + latest comments
- Key error/repro details if present
- Whether there is an active linked PR/fix path

## Output

Return 1-3 sentences focused on what is happening and why it matters.

Do not include extra sections, bullets, or markdown tables in the final summary.
