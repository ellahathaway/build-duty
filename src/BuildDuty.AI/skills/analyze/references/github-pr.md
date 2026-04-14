# Reference: GitHub Pull Request

Use this when the signal type is a GitHub pull request signal (`github-pr`).

## Context awareness

Check the signal's `context` field. It describes what these PRs are for and how they relate to other signal sources (e.g., pipelines, issues). Use it to understand significance.

## Signal info

Use stored signal payload only (metadata, review status, checks, and state).
Do not fetch external data during summarization.

## Extract cause, effect, and evidence

**Cause**: Why this PR is in its current state. Examples:
- `CI failing: sdk-diff test regression after dotnet/dotnet#45231 merged`
- `PR blocked on required review from @dotnet/source-build-internal`
- `PR merged: fixes Alpine 3.23 OpenSSL SHA-1 issue by updating crypto policy`

**Effect**: What this PR's state means. Examples:
- `Fix for source-build Alpine failure is pending CI validation`
- `Blocked PR delays .NET 10 preview 4 source-build release`
- `Fix merged; source-build should pass on next unified-build run`

**Evidence**: Raw details for cross-signal correlation. Always include:
- PR number, repository, and URL (e.g., `dotnet/dotnet#567`)
- Target branch
- Any referenced issues, other PRs, pipeline names, or build numbers from PR title/body/comments
- CI/check status — especially failing required checks and their names
- Review status (approvals, requested changes, pending reviews)
