# Reference: GitHub Issue

Use this when the signal type is a GitHub issue signal (`github-issue`).

## Context awareness

Check the signal's `context` field. It describes what this issue source tracks and how it relates to other signal sources (e.g., pipelines, PRs). Use it to understand significance.

## Signal info

Use stored signal payload only (metadata, body, comments, and state).
Do not fetch external data during summarization.

## Extract cause, effect, and evidence

**Cause**: Why this issue exists or why it's in its current state. Examples:
- `Alpine 3.23 ships OpenSSL 3.5 which drops SHA-1 support; dotnet-runtime HMACSHA1 tests fail`
- `Source-build fails on Fedora 43 due to missing libicu-devel package in container image`
- `Issue opened to track recurring NuGet timeout in dnceng internal feed`

**Effect**: What this issue means for the project. Examples:
- `Source-build broken on Alpine 3.23; blocks .NET 10 preview 4 release`
- `No workaround available; requires upstream OpenSSL config change`

**Evidence**: Raw details for cross-signal correlation. Always include:
- Issue number and repository (e.g., `dotnet/runtime#98765`)
- Any referenced PRs, other issues, pipeline names, or build numbers from the issue body and comments
- Key error messages or repro details from the issue body
- Labels, milestone, assignees
- Whether there is an active linked PR or fix path
