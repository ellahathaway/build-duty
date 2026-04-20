---
name: analyze-signal
description: Analyze a signal and persist structured analyses. Determines why the signal was collected, based on the signal type and the information available. Use when a single signal needs analysis.
---

# Analyze

Single-signal analyses — extract evidence and produce an analysis for each distinct element of a signal, then persist those analyses back to the signal record.

## References

Use the matching reference based on signal type:
- [references/azure-devops-pipeline.md](./references/azure-devops-pipeline.md) -> AzureDevOpsPipeline
- [references/github-pr.md](./references/github-pr.md) -> GitHubPullRequest
- [references/github-issue.md](./references/github-issue.md) -> GitHubIssue

## Workflow

1. Load the signal data for the provided `signalId`.
2. Determine the signal type and use the matching reference document for type-specific instructions.
3. Note the signal's context field if provided — it describes what this signal source is and what it depends on.
4. Load the signal's existing analyses (the `analyses` array on the signal).
5. Compare the current signal information against existing analyses to determine what action to take for each:
  - **Still accurate** — an existing analysis describes the same root cause and details are unchanged. Call `update_signal_analysis` with the existing analysis ID but **omit** `analysisData` and `analysis` to stamp the current triage run without changing content.
  - **Needs updating** — an existing analysis covers the same root cause but details have changed that require an update to the analysis content. Call `update_signal_analysis` with the existing analysis ID and updated content.
  - **Resolved** — an existing analysis is no longer relevant or accurate. Call `resolve_signal_analysis` with the existing analysis ID and the reason for resolution.
6. For any new active analysis that has no related existing analyses, call `create_signal_analysis`. Do not create a new analysis when the analysis simply shows that a previously-tracked analysis is now resolved — resolve the existing analysis instead.
7. If the signal has zero analyses (none existed before and no new analyses were found), create a minimal analysis describing the signal's current state.

## Output

No text output is required. The CLI determines result counts by inspecting the persisted analyses after this skill completes.
