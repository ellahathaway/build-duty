# Reference: GitHub Pull Request (SignalType.GitHubPullRequest)

Applies when `signal.Type == SignalType.GitHubPullRequest`. PR details are in `signal.Info` (`JsonElement`).

---

## Classify the PR

Check `signal.Context` for monitoring rationale and correlation hints (related pipelines, issues, branches).

From `signal.Info`, determine the PR's situation:

- **CI / checks failing** — required checks failing.
- **Blocked on review** — approvals missing or changes requested.
- **Merge blocked by policy** — conflicts, failing checks, or missing sign-offs.
- **Ready / passing** — checks passing, reviews sufficient.
- **Merged / closed** — the signal indicates resolution when the PR has been merged or closed. A merged PR means the change has landed and any issues it was tracking (failing checks, review blocks) are no longer active. A closed-without-merge PR may indicate the approach was abandoned — check the signal context and comments for why.

If the info is insufficient, use the GitHub MCP to pull additional information.

Note whether the PR has **one** root cause or **multiple** independent problems (each becomes a separate analysis).

---

## Analysis data — always include:

| Field | Source |
|---|---|
| `failingChecks` | Names + states of failing required checks (when applicable) |
| `relatedLinks` | Referenced issues, PRs, pipeline runs, or build URLs |
| `blockingReasons` | Review/policy/conflict blockers (when applicable) |

Example — CI failing:
```json
{
  "failingChecks": ["sdk-diff", "source-build (Alpine 3.23)"],
  "relatedLinks": ["https://github.com/dotnet/runtime/issues/12345"]
}
```

### Analysis text — concise cause statement

Examples:
- `CI failing: sdk-diff test regression after dotnet/dotnet#45231 merged.`
- `PR blocked on required review from @dotnet/source-build-internal.`
