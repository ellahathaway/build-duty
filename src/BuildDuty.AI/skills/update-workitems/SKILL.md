---
name: update-workitems
description: Takes the collected signals and their analyses from a triage run and reconciles them with existing work items — evaluating links, adopting unlinked analyses, and resolving fully-resolved work items.
---

# Update Work Items

Reconcile analyses from a triage run with existing work items.

## Input

You receive:
- A **triage run ID** (`triage_{guid}`)

## Setup

1. Use `list_analyses_for_triage` to get all analyses that changed during this triage run.
2. Use `list_work_items` (state: `unresolved`) to load all unresolved work items.

If there are no analyses for this triage run, return immediately with `{"workItemsUpdated": 0, "workItemsResolved": 0}`.

## Context

Each work item has a `LinkedAnalyses` list — entries of `(SignalId, AnalysisIds[])`. Each analysis ID points to a specific analysis entry on a signal. Analyses have a `Status` (new, updated, or resolved) — resolved analyses stay linked and preserve provenance.

Use [references/incident-grouping.md](./references/incident-grouping.md) for correlation criteria throughout.

## Per-analysis reconciliation

For each analysis from the triage run, load it with `get_analysis` and use `get_work_items_for_analysis` to determine if it is linked to any work items.

### Linked analyses — evaluate existing links

If the analysis is linked to one or more work items:

1. For each linked work item, compare the analysis against the work item's `IssueSignature`, `Summary`, and evidence in other linked analyses using the correlation criteria.
   - **Still correlates** — keep it linked. If the analysis is resolved, do NOT unlink — resolved analyses stay linked for provenance.
   - **No longer correlates** — unlink it. If the signal has zero remaining linked analysis IDs on that work item, it is fully unlinked.

2. If the analysis was unlinked, attempt to find a better match among unresolved work items (same as the unlinked flow below).

### Unlinked analyses — find a matching work item

If the analysis is not linked to any work item (either newly created or just unlinked above):

1. Compare it against every unresolved work item using the correlation criteria (same-cause grouping, causal chain detection, cross-type correlation). Additionally check:
   - **Evidence cross-references** — load the work item's existing linked analyses (via `get_analysis`) and compare evidence fields: build IDs, pipeline URLs, run IDs, repository names, issue/PR numbers, and error signatures. A shared build ID, pipeline reference, or issue link is strong evidence of the same incident.
   - **Cross-type correlation** — signals of different types (AzDo pipeline, GitHub issue, GitHub PR) frequently describe the same incident from different angles. A GitHub issue that references a failing build URL, or a PR linked to a tracked pipeline, should match the work item tracking that pipeline (and vice versa).

2. Evaluation:
   - **Match found** — link the analysis (signal ID + analysis ID) to that work item. Update metadata if it adds new evidence.
   - **No match** — leave unlinked. The create-workitems step will handle it.

## Metadata refresh

For each work item whose links changed, update `IssueSignature`, `Summary`, or `CorrelationRationale` if the remaining active analyses provide better evidence.

## Resolution

For each unresolved work item that was touched during reconciliation, check:
- **No linked analyses** — all analyses were unlinked. Resolve the work item with reason "all analyses unlinked".
- **All resolved** — every linked analysis has `status: resolved`. Resolve the work item.
- **Mixed** — some analyses are still new or updated. Keep unresolved.

## Output

Return **only** the raw JSON object below — no markdown fences, no commentary, no extra text.

```json
{
  "workItemsUpdated": 3,
  "workItemsResolved": 1
}
```
`workItemsUpdated` counts work items whose links or metadata changed. `workItemsResolved` counts work items resolved. A work item that was both updated and resolved counts only in `workItemsResolved`.
