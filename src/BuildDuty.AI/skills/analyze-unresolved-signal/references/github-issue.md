# Reference: GitHub Issue (SignalType.GitHubIssue)

Applies when signal type is a GitHub issue. Issue details are in `signal.Info` (`JsonElement`).

---

## Analyze Signal

Applies to signals that were collected as `New` and `Updated`.

### If `collectionReason` is `New`

- Identify each distinct active problem or tracker scope from the current issue body/comments.
- Create analyses for each distinct finding.

### If `collectionReason` is `Updated`

- Re-evaluate the issue using the latest body/comments/context.
- For each existing matching analysis, update the analysis text and data to reflect current evidence, even if the root cause is unchanged.
- Resolve analyses that are no longer supported by the current issue state.
- Create analyses for newly introduced independent problems.

From the signal's info, determine:

- **Active problem** — issue describes an unresolved bug/regression/outage/build break.
- **Tracker / meta issue** — aggregates links to other issues/PRs/failures rather than one concrete problem.

If the info is insufficient, use the GitHub MCP to pull additional information.

Note whether the issue describes **one** root cause or **multiple** independent failures (each becomes a separate analysis).

### Analysis data — always include:

| Field | Source |
|---|---|
| `errorMessages` | Concrete error/failure/warning text from the issue body/comments (when present) |
| `affectedComponents` | Repos, packages, pipelines, or platforms mentioned |
| `relatedLinks` | Referenced PRs, issues, or build URLs |

Example — open build break:
```json
{
  "errorMessages": [
    "System.Security.Cryptography.CryptographicException: The operation is not supported on this platform."
  ],
  "affectedComponents": ["dotnet/runtime", "Alpine 3.23", "OpenSSL 3.5"]
}
```

### Analysis text — concise cause statement

Check the signal context, if available, for monitoring rationale and correlation hints (related pipelines, PRs, branches).

For `Updated` signals, avoid reusing stale wording from prior triage runs. Rewrite the analysis text to match the newest available evidence and scope.

Examples:
- `Alpine 3.23 ships OpenSSL 3.5 which drops support for legacy algorithms; cryptography tests fail with CryptographicException on Alpine CI legs.`

For tracker/meta issues with insufficient detail for a concrete cause, describe the tracking scope: `Tracking issue for .NET 10 source-build failures; links 3 pipeline issues and 2 PRs but no single root cause identified.`
