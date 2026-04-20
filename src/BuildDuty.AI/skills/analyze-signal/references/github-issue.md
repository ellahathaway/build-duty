# Reference: GitHub Issue (SignalType.GitHubIssue)

Applies when signal type is a GitHub issue. Issue details are in the signal's info.

---

## Classify the issue

Check the signal context, if available, for monitoring rationale and correlation hints (related pipelines, PRs, branches).

From the signal's info, determine:

- **Active problem** — issue is open; describes an unresolved bug/regression/outage/build break.
- **Resolved / mitigated** — the signal indicates resolution when the issue is closed, or the issue body/comments clearly state the problem is fixed or mitigated (e.g., a fix has been merged, a workaround applied, or the underlying cause was addressed externally). The root causes described by existing analyses are no longer active.
- **Tracker / meta issue** — aggregates links to other issues/PRs/failures rather than one concrete problem.

If the info is insufficient, use the GitHub MCP to pull additional information.

Note whether the issue describes **one** root cause or **multiple** independent failures (each becomes a separate analysis).

---

## Analysis data — always include:

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

Examples:
- `Alpine 3.23 ships OpenSSL 3.5 which drops support for legacy algorithms; cryptography tests fail with CryptographicException on Alpine CI legs.`

For tracker/meta issues with insufficient detail for a concrete cause, describe the tracking scope: `Tracking issue for .NET 10 source-build failures; links 3 pipeline issues and 2 PRs but no single root cause identified.`
