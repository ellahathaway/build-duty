---
name: analyze-signal
description: Analyze a signal and persist structured analyses. Determines why the signal was collected and what the likely root cause is, based on the signal type and the information available. Use when a single signal needs analysis.
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
2. Determine signal type and use the matching reference document for instructions and evidence selection.
3. Note the signal's context field — it describes what this signal source is and what it depends on.
4. Analyze the current signal according to the matching reference. Determine the set of analyses you would persist.
5. Before persisting, **load the signal's existing analyses** (the `analyses` array on the signal). Compare each existing analysis against your new set by matching on root cause:
   - **Still accurate** — an existing analysis describes the same root cause as one of your new analyses *and* the details are unchanged. **Do nothing** — leave it in place. Do not call `create_signal_analysis`. Count it in neither created nor updated.
   - **Needs updating** — an existing analysis covers the same root cause but details have changed (e.g., different error text, new evidence). Call `update_signal_analysis` with the existing analysis ID. Count as `analysesUpdated`.
   - **No longer relevant** — an existing analysis describes a root cause not present in your new set. Call `remove_signal_analysis`. Count as `analysesRemoved`.
6. For each new analysis that has **no matching existing analysis**, call `create_signal_analysis`. Count as `analysesCreated`.

## Output

Return **only** the raw JSON object below — no markdown fences, no commentary, no extra text.

```json
{
  "analysesUpdated": 1,
  "analysesCreated": 2,
  "analysesRemoved": 0
}
```
`analysesUpdated` counts existing analyses whose details were updated. `analysesCreated` counts newly persisted analyses. `analysesRemoved` counts existing analyses that were removed as no longer relevant.
