---
name: create-workitems
description: Takes orphaned analyses from a triage run, groups them by root cause, and creates a new work item for each group.
---

# Create Work Items

After existing work items have been updated, some analyses remain orphaned — not linked to any work item. This skill groups those orphaned analyses by root cause and creates a new work item for each group.

## Input

You receive:
- A **triage run ID** (`triage_{guid}`)

## Step 1 - Collect orphaned analyses

Keep a list of analyses and are not linked to any work item (orphaned analyses).

List all signal analyses from the triage run. For each analysis:
1. List the ids of work items linked to the analysis. If there are no linked work items, continue to the next step. Otherwise, skip this analysis.
2. Get the specific analysis. Determine if the analysis is resolved by examining the `Status` field in the analysis. If the analysis is not resolved, continue to the next step. Otherwise, skip this analysis.
3. Add the analysis to the list of orphaned analyses.

## Step 2 - Group analyses by root cause

Using [references/incident-grouping.md](./references/incident-grouping.md) as a guide for grouping, compare all orphaned analyses from the previous step and form groups that share the same underlying issue.

Guidance:
- Analyses from different signals can be in the same group if they describe the same root cause or are otherwise closely related.
- A single signal may contribute analyses to multiple groups if its analyses describe different root causes.
- When uncertain of grouping, split into separate groups rather than merge.
- Every orphaned analysis must appear in at least one group.

## Step 3 - Create Work Items

For each group created in Step 2, create a work item.

## Output

No text output is required.
