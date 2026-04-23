---
name: analyze-unresolved-signal
description: Analyze an unresolved signal. Determines why the signal was collected, based on the signal type and the information available. Use when a single signal needs a new or updated analysis. Never use for signals with resolved collection reasons (`Resolved`, `NotFound`, `OutOfScope`).
---

# Analyze Unresolved Signal

Extract evidence and produce an analysis for each distinct element of an unresolved signal (collected for the first time or updated), then save those analyses back to the signal record.

## Context

- Signal: A unit of observation that represents a specific event or condition in a system, which requires further analysis to determine its cause or significance.
- Analysis: A structured assessment of a signal that identifies the underlying problem or condition, the evidence supporting this assessment, and any relevant context or details that help in understanding the signal's significance. A signal can have multiple analyses, each corresponding to a distinct element or aspect of the signal.

## References

Use the matching reference based on signal type:
- references/azure-devops-pipeline.md -> AzureDevOpsPipeline
- references/github-pr.md -> GitHubPullRequest
- references/github-issue.md -> GitHubIssue

## Prerequisites

Only signals with `collectionReason` of `New` or `Updated` are sent to this skill. If a signal has a resolved collection reason (`Resolved`, `NotFound`, `OutOfScope`), exit the skill without performing any analysis.

Use the `get_signal` tool to load the signal by its ID. This provides the current signal state, including existing analyses.

Capture `collectionReason` immediately after loading the signal and follow the matching workflow below.

## Analysis Workflow

### If `collectionReason` is `New`

1. Analyze the current signal using the reference document for its type.
2. For each distinct active problem/tracker element found, create a new signal analysis.

### If `collectionReason` is `Updated`

1. Analyze the current signal using the reference document for its type.
2. Compare each current finding to existing active (non-resolved) analyses.
3. If an existing analysis still matches the same underlying problem:
   1. Update the signal analysis to refresh the analysis text and data to the newest evidence.
   2. Update the signal analysis even if the root cause did not change. This ensures that the analysis reflects the most recent evidence and context.
4. If an existing analysis no longer matches any current finding, resolve the signal analysis with a short reason.
5. For any new current finding with no related existing analysis, create a new signal analysis.

## Post-Analysis Checks

After you've completed the analysis for the signal, complete the following checks:
1. The signal has at least one analysis associated with it.
2. At least one analysis in the signal has a `lastTriageId` matching the current triage run.
3. If any check fails, fix the analysis by creating or updating the signal analysis before finishing.

## Output

No text output is required.
