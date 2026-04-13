---
name: update-linked-work-items
description: Updates unresolved work items that are linked to signals in this triage run, verifying that linked analyses still match the work item's issue and updating metadata or unlinking as needed.
---

# Update Linked Work Items

For each work item with linked signals in this triage run, verify that the specific linked analyses still correlate with the work item's root cause. Update, prune, or unlink as needed.

## Input

You receive:
- A **triage run ID** (`triage_{guid}`)

## Context

Each work item has a `LinkedAnalyses` list — entries of `(SignalId, AnalysisIds[])`. Each analysis ID points to a specific analysis entry on a signal. The goal is to ensure every linked analysis still describes the same root cause tracked by the work item.

## Steps

1. List unresolved work items that have signals in this triage run.
2. For each work item, load each linked signal and inspect the analyses whose IDs appear in the work item's `LinkedAnalyses` for that signal.
3. **Per-analysis evaluation** — compare each linked analysis against the work item's `IssueSignature` and `Summary`:
   - **Still correlates** — the analysis describes the same root cause. Keep it linked.
   - **No longer correlates** — the root cause changed to something unrelated. Remove that analysis ID from the link. (The grouping step will pick it up for other work items.)
   - **Analysis deleted** — the analysis ID no longer exists on the signal. Remove it from the link.
   - **Analysis resolved** — the analysis now indicates the previous issue is resolved. Do not unlink just for this reason — resolution is handled by the reconcile step. Keep it linked until then.
4. **Signal-level cleanup** — after pruning analysis IDs, if a signal has zero remaining linked analysis IDs to the work item, unlink the signal entirely from the work item.
5. **Metadata refresh** — for analyses that remain linked, check if they provide additional evidence or a clearer root cause than what the work item currently captures. If so, update `IssueSignature`, `Summary`, or `CorrelationRationale`.
6. Do not unlink a signal just because it resolved — resolution is handled by the reconcile step.
7. Return a summary:
```json
{
  "workItemsUpdated": 3,
  "analysesUnlinked": 7,
  "signalsFullyUnlinked": 2
}
```
