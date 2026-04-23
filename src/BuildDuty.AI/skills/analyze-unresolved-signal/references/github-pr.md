# Reference: GitHub Pull Request (SignalType.GitHubPullRequest)

Applies when signal's type is an GitHub pull request . PR details are in `signal.Info` (`JsonElement`).

---

## Analyze Signal

Applies to signals that were collected as `New` and `Updated`.

### If `collectionReason` is `New`

- Identify each distinct PR problem state from current checks/reviews/policy status.
- Create analyses for each distinct finding.

### If `collectionReason` is `Updated`

- Re-evaluate the PR using the latest checks, reviews, mergeability, and context.
- For each existing matching analysis, update the analysis text and data to reflect current evidence, even if the underlying blocker category is unchanged.
- Resolve analyses that are no longer supported by the current PR state.
- Create analyses for newly introduced independent blockers/failures.

From `signal.Info`, determine the PR's situation:

- **CI / checks failing** — required checks failing.
- **Blocked on review** — approvals missing or changes requested.
- **Merge blocked by policy** — conflicts, failing checks, or missing sign-offs.
- **Ready / passing** — checks passing, reviews sufficient.

If the info is insufficient, use the GitHub MCP to pull additional information.

Note whether the PR has **one** root cause or **multiple** independent problems (each becomes a separate analysis).

### Analysis data — always include:

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

Check `signal.Context` for monitoring rationale and analysis hints (related pipelines, issues, branches).

For `Updated` signals, rewrite the analysis text to match the latest PR state instead of reusing stale wording from prior triage runs.

Examples:
- `CI failing: sdk-diff test regression after dotnet/dotnet#45231 merged.`
- `PR blocked on required review from @dotnet/source-build-internal.`
