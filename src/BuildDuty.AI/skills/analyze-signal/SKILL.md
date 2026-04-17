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
   - **Needs updating** — an existing analysis covers the same root cause but details have changed (e.g., different error text, new evidence, different failing leg). Call `update_signal_analysis` with the existing analysis ID. Count as `analysesUpdated`.
   - **Resolved** — an existing analysis should be resolved when:
     - The root cause it describes is **no longer active** (e.g., pipeline now succeeds, issue closed, PR merged).
     - It has been **superseded** by a new, more accurate analysis (e.g., initial analysis said "NuGet timeout" but deeper investigation reveals "DNS infrastructure outage causing NuGet timeout" — the old analysis isn't wrong but is now covered by the new one).
     Call `resolve_signal_analysis` with the existing analysis ID and the resolution criteria that were met. Count as `analysesResolved`.
6. For each new analysis that has **no matching existing analysis** (including resolved ones — a new active failure distinct from a previously resolved one gets a new analysis), call `create_signal_analysis`. Count as `analysesCreated`.
7. **Every signal must have at least one analysis after this workflow completes.** If your analysis produced no new causes and no existing analyses remain (active or resolved), create an analysis describing the signal's current healthy state via `create_signal_analysis` (e.g., "PR is passing all checks and ready to merge", "Pipeline succeeded with no active failures").

## Output

No text output is required. The CLI determines result counts by inspecting the persisted analyses after this skill completes.
