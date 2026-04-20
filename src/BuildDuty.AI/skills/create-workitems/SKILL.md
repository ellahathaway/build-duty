---
name: create-workitems
description: Takes orphaned analyses from a triage run, groups them by root cause, and creates a new work item for each group.
---

# Create Work Items

After existing work items have been updated, some analyses remain orphaned — not linked to any work item. This skill groups those orphaned analyses by root cause and creates a new work item for each group.

## Input

You receive:
- A **triage run ID** (`triage_{guid}`)

## Steps

### 1. Collect orphaned analyses

- Use `list_analyses_for_triage` (linkedStatus: `unlinked`) to get analyses on triage signals that are not linked to any work item.
  - Each work item has a `LinkedAnalyses` list — entries of `(SignalId, AnalysisIds[])`. Each analysis ID points to a specific analysis entry on a signal. Analyses have a `Status` (new, updated, or resolved) — resolved analyses stay linked and preserve provenance.
- After listing the analyses, discard any with `status: Resolved` — resolved analyses do not need work items. If none remain after filtering, return early with zeros.

### 2. Load each orphaned analysis

For each orphaned entry, load the analysis using `get_analysis` by its signal ID and analysis ID.

### 3. Group by root cause

Using [references/incident-grouping.md](./references/incident-grouping.md), compare root causes across all orphaned analyses and form groups that share the same underlying issue.

- Analyses from different signals can be in the same group if they describe the same root cause.
- A single signal may contribute analyses to multiple groups if its analyses describe different root causes.
- When uncertain, split into separate groups rather than merge.
- Every orphaned analysis must appear in exactly one group.

### 4. Create a work item per group

For each group, using [references/issue-writing.md](./references/issue-writing.md):

1. Create a new work item with the triage ID, `summary`, `issueSignature`, and the group's `linkedAnalyses` (signal ID + analysis ID pairs).

## Output

No text output is required. The CLI determines result counts by inspecting work item state after this skill completes.
