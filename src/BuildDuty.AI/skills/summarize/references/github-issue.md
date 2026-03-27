# Reference: GitHub Issue

## When to use

Use this when the signal type is a GitHub issue signal (`github-issue`).

## Signal info

Use stored signal payload only (metadata, body, comments, and state).
Do not fetch external data during summarization.

## Summary focus

- Issue title/number and state (open/closed)
- Most relevant problem details from body + latest comments
- Key error/repro details if present
- Whether there is an active linked PR/fix path

## Keep it concise
- 1 sentence preferred, max 2.
- Focus on current issue state and top actionable problem detail.
- No markdown or bullet formatting in final summary text.
