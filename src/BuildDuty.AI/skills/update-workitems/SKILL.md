---
name: update-workitems
description: Reconciles signal analyses from a triage run with unresolved work items by keeping, moving, or adopting analysis links and refreshing work item metadata when correlated evidence changes.
---

# Update Work Items

Updates existing work items based on the analyses from a triage run by linking new analyses to work items, unlinking analyses that no longer correlate, and refreshing work item metadata with newly correlated evidence.

## Input

You receive:
- A **triage run ID**

## Step 1 - List Work Items and Analyses

List all work item IDs filtered to unresolved items. If there are no unresolved work items, stop — no further action is needed.

Simultaneously, list all of the analyses that were touched during the specified triage run. If there are no analyses for this triage run, stop — no further action is needed.

## Step 2 - Compare Analyses with Work Items

Keep a small JSON scratch structure during this run:

```json
{
   "decisions": [
      {
         "signalId": "signal-123",
         "analysisId": "analysis-456",
         "decision": "keep",
         "fromWorkItemId": "wi-001"
      },
      {
         "signalId": "signal-456",
         "analysisId": "analysis-789",
         "decision": "move",
         "fromWorkItemId": "wi-002",
         "toWorkItemId": "wi-003"
      },
      {
         "signalId": "signal-789",
         "analysisId": "analysis-101112",
         "decision": "adopt",
         "toWorkItemId": "wi-004"
      },
      {
         "signalId": "signal-101112",
         "analysisId": "analysis-131415",
         "decision": "no-target"
      },
      {
         "signalId": "signal-131415",
         "analysisId": "analysis-161718",
         "decision": "remove",
         "fromWorkItemId": "wi-004"
      }
   ],
   "refreshWorkItemIds": ["wi-001", "wi-002", "wi-003", "wi-004"]
}
```

Use this only as temporary scratch state for the current run.

For each signal-analysis gathered from Step 1:
1. Get the specific analysis by its signal ID and analysis ID.
2. Determine whether the analysis is resolved/new/updated. If resolved, skip it.
3. List all of the work items currently linked to this analysis. Determine which of these are unresolved.
4. Compare the analysis against all unresolved work items (not just the linked ones) using [references/incident-grouping.md](./references/incident-grouping.md), then choose one best target:
   1. If one or more unresolved correlating work items are already linked to this analysis, choose the oldest of those linked work items as the best target.
   2. Otherwise choose the oldest unresolved correlating work item as the best target.
   3. If no unresolved work item correlates, there is no target work item for the analysis.
5. Apply one decision:
   1. **keep**: current linked unresolved work item is still the best target.
   2. **move**: a different unresolved work item is the best target.
   3. **adopt**: no unresolved link exists but a best target exists.
   4. **remove**: the analysis is linked today, but it no longer correlates with any unresolved work item and should be unlinked from its current unresolved work item.
   5. **no-target**: no unresolved work item correlates and there is nothing to unlink.
6. Record the decision in the scratch JSON structure. If the decision is **keep**, **move**, **adopt**, or **remove**, also record the affected work item ID or IDs in `refreshWorkItemIds`.
7. Do not remove links for resolved analyses.

## Step 3 - Adopt Orphaned, Unlinked, and Rehomed Analyses

Apply Step 2 decisions:
1. For each **move** decision, unlink the analysis from the old unresolved work item and link it to the new best target work item.
2. For each **adopt** decision, link the analysis to the best target work item.
3. For each **remove** decision, unlink the analysis from the old unresolved work item.
4. For each **no-target** decision, leave the analysis unlinked.
5. Do not resolve any work item in this skill; later resolution flow handles obsolete/empty work items.

## Step 4 - Refresh Correlated Work Item Metadata

For each work item ID in `refreshWorkItemIds`:
1. Review the linked analyses and synthesize a single best current summary and issue signature.
2. Update the work item metadata only when the summary and issue signature should change based on reviewing the linked analyses.
3. Omit unchanged or empty summary and issue signature fields so existing values are preserved.

## Output

No text output is required.
