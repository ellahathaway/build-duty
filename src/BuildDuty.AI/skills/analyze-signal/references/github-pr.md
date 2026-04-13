# Reference: GitHub Pull Request (SignalType.GitHubPullRequest)

Applies when `signal.Type == SignalType.GitHubPullRequest`. PR details are in `signal.Info` (`JsonElement`).

Persist the signal analysis once per distinct cause (e.g., separate failing checks, review blocks).

---

## Classify the PR

Check `signal.Context` for monitoring rationale and correlation hints (related pipelines, issues, branches).

From `signal.Info`, determine the PR's situation:

- **CI / checks failing** — required checks failing.
- **Blocked on review** — approvals missing or changes requested.
- **Merge blocked by policy** — conflicts, failing checks, or missing sign-offs.
- **Ready / passing** — checks passing, reviews sufficient.
- **Merged / closed** — PR landed or was closed.

If the info is insufficient, use the GitHub MCP to pull additional information.

Note whether the PR has **one** root cause or **multiple** independent problems (each becomes a separate analysis).

---

## Create analyses

Persist the signal analysis per distinct cause.

#### `analysisData` — always include:

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

Example — merged PR:
```json
{
  "relatedLinks": ["https://github.com/dotnet/runtime/issues/12345"]
}
```

#### `analysis` — concise cause statement

Examples:
- `CI failing: sdk-diff test regression after dotnet/dotnet#45231 merged.`
- `PR blocked on required review from @dotnet/source-build-internal.`
- `PR merged: fixes Alpine 3.23 OpenSSL SHA-1 issue. Source-build should pass on next unified-build run.`
