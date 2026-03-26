# Reference: Azure DevOps Pipeline

## When to use

Use this when the signal type is an azure devops pipeline pipeline run signal.

## Signal info

Signal info includes run metadata and timeline records.

Use signal info as the primary source of truth.
Timeline records in signal info match the pipeline run result. Use these to narrow down the summary.

Only summarize stages/jobs/tasks that are present in signal info timeline records.
Do not include results from other stages/jobs that are not present in those records.

## Identify the signal

- URL formats:
	- `https://dev.azure.com/{org}/{project}/_build/results?buildId={id}`
	- `https://{org}.visualstudio.com/{project}/_build/results?buildId={id}`

Extract `org`, `project`, and `buildId` from the URL.

## Additional lookup

Only fetch additional data if required context is missing from signal info.
You can pull timeline logs when needed to help diagnose and summarize the run,
but only for records that are present in signal info timeline records.

## Summary focus

- Prioritize timeline results/status from signal info; treat these as the primary source for the summary.
- Use build result/status (`failed`, `partiallySucceeded`, `succeeded`, `canceled`) as secondary context.
- For any timeline record that is not fully successful (including `SucceededWithIssues`), identify the exact point (`Stage > Job > Task`) and explain why it has that result.
- Do not stop at status labels. Include the concrete cause from record issues or timeline logs (warning/error text, failed check, or command output).
- If a non-success record has empty/insufficient `Issues`, fetch the corresponding timeline log and extract the warning/error that caused that outcome.
- Include the most actionable warning/error details from the included timeline records and their logs.
- Include impact/context (blocked stages/jobs or cancellation point) based on the included timeline records.

### Timeout focus

If timeout is indicated, state which job/task timed out and what it was running when canceled.

## Output

Return 1-3 sentences focused on what happened, why it happened, and where it happened.

If a build and/or timeline record did not succeed, explicitly state the reason what warning/error caused that status.

Do not include extra sections, bullets, or markdown tables in the final summary.
