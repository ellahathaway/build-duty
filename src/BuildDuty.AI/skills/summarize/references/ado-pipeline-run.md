# Reference: ado-pipeline-run signal

This reference applies when a work item has a signal of type `ado-pipeline-run`.
The signal `ref` is a URL to an Azure DevOps build result.

## Extracting build info from URLs

ADO build URLs follow these patterns:
- `https://dev.azure.com/{org}/{project}/_build/results?buildId={id}`
- `https://{org}.visualstudio.com/{project}/_build/results?buildId={id}`

Extract the **org name**, **project name**, and **build ID** from the URL.

## Pipeline hierarchy

```
Pipeline (Definition)
└── Build (Run)
    └── Stage
        └── Job / Phase
            └── Task / Step
```

## Summarizing by outcome

Adapt the summary based on the build result:

### failed

Focus on **what broke and why**. Do NOT just report the outcome — dig into the
build to find the actual failure.

**Required steps:**
1. Fetch the build timeline to find the first failed task
2. Walk the hierarchy: failed task → parent job → parent stage
3. Read the task's log to extract the actual error output
4. Quote key error messages verbatim (compiler errors, test names, exceptions)

**What to include:**
- **Failed Step** — the specific task that failed (e.g. `Build`, `VSTest`,
  `DotNetCoreCLI`), including the full path: `Stage > Job > Task`
- **Error Details** — the actual error message from the logs; for test failures
  list the failing test names; for compilation errors quote the CS/FS/VB error codes
- **Blast Radius** — which stages/jobs were skipped or blocked by this failure
- **Suggested Next Steps** — retry, investigate a specific test, check a PR, etc.

### partiallySucceeded

Focus on **what passed vs. what didn't**.

- **Passed Stages** — brief list of stages that succeeded
- **Failed / Warning Stages** — for each, list failed or warning jobs and errors
- **Impact** — is the partial success blocking anything? Are failures in optional legs?
- **Suggested Next Steps** — which failures to investigate vs. ignore

### succeeded

Focus on **health and notable observations**.

- **Stage Summary** — list stages with durations; highlight unusually slow ones
- **Warnings** — any `succeededWithIssues` tasks worth noting
- **Comparison** — if asked, compare duration/warnings to previous runs

### canceled

Focus on **why and what was in-flight**.

- **Cancellation Point** — which stage/job was running when canceled
- **Possible Reason** — user-initiated, superseded by newer run, timeout, etc.
- **Suggested Next Steps** — re-run, check if a newer build covers this, etc.

## Common failure patterns

| Pattern | Indicators |
|---------|-----------|
| Compilation error | `Csc` or `Build` task failed with CS* error codes |
| Test failure | `VSTest` or `DotNetCoreCLI` test task failed |
| Infrastructure | `checkout`, `Download`, or agent tasks failed |
| Timeout | Task `result=canceled` with timeout message |
| Dependency | `NuGetCommand` or `DotNetCoreCLI restore` failed |

## Build result values
- `succeeded` — all stages passed
- `partiallySucceeded` — some stages failed but the build completed
- `failed` — the build failed
- `canceled` — the build was canceled

## Timeline record fields
- `name` — display name
- `type` — `Stage`, `Job`, `Task`
- `state` — `pending`, `inProgress`, `completed`
- `result` — `succeeded`, `succeededWithIssues`, `failed`, `canceled`, `skipped`, `abandoned`
- `parentId` — links to parent record (job → stage, task → job)
- `issues` — array of error/warning messages
