---
name: update-workitems
description: Takes the collected signals and their analyses from a triage run and updates existing work items accordingly — fixing stale links, adopting orphaned analyses, and resolving work items.
---

# Update Work Items

Maintain work items in three sequential phases: fix stale links, adopt orphaned analyses, and resolve work items.

## Input

You receive:
- A **triage run ID** (`triage_{guid}`)

## Early exit

List unresolved work items. If there are **none**, skip all phases and return immediately with `{"workItemsUpdated": 0, "workItemsResolved": 0}`.

## Context

Each work item has a `LinkedAnalyses` list — entries of `(SignalId, AnalysisIds[])`. Each analysis ID points to a specific analysis entry on a signal.

## Phase 1 — Fix existing links

1. List unresolved work items that have linked signals in this triage run, filtered to only the `LinkedAnalyses` entries whose signal is in the run.
2. For each work item, load each linked analysis by its signal ID and analysis ID. Inspect the analysis content against the work item.
3. **Per-analysis evaluation** — compare each linked analysis against the work item's `IssueSignature` and `Summary`:
   - **Still correlates** — keep it linked.
   - **No longer correlates** — remove that analysis ID from the link.
   - **Analysis deleted** — the analysis ID no longer exists on the signal. Remove it from the link.
4. **Signal-level cleanup** — if a signal has zero remaining linked analysis IDs, unlink the signal entirely.
5. **Metadata refresh** — if remaining linked analyses provide better evidence, update `IssueSignature`, `Summary`, or `CorrelationRationale`.

## Phase 2 — Link orphaned analyses

6. List orphaned analyses for this triage run — analyses on triage signals that are not linked to any work item.
7. For each orphaned analysis, compare its root cause against the `IssueSignature` and `Summary` of every unresolved work item.
   - **Match found** — link the analysis (signal ID + analysis ID) to that work item. Update metadata if it adds new evidence.
   - **No match** — leave unlinked. The grouping step will handle it.

## Phase 3 — Resolve

8. List unresolved work items that were updated in this triage run.
9. For each, load its linked analyses and check whether **all** now indicate the underlying issue is resolved (e.g., a previously-failing pipeline is passing, an issue is closed).
   - **All resolved** — resolve the work item.
   - **Mixed** — keep unresolved; some evidence still shows the issue is active.

## Output

Return **only** the raw JSON object below — no markdown fences, no commentary, no extra text.

```json
{
  "workItemsUpdated": 3,
  "workItemsResolved": 1
}
```
`workItemsUpdated` counts work items whose links or metadata changed (phases 1–2). `workItemsResolved` counts work items resolved (phase 3). A work item that was both updated and resolved counts only in `workItemsResolved`.
