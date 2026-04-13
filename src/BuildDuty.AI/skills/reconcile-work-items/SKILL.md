---
name: reconcile-work-items
description: Reconciles triage-run signal deltas into work items through grouping, metadata writing, create/link/update, and evidence-based resolve/reopen decisions. Use for end-to-end work item lifecycle processing.
---

# Reconcile Work Items

Starter workflow for end-to-end work item lifecycle reconciliation.

## Scope
- This skill orchestrates the full reconciliation flow.
- This skill applies tools directly and uses referenced guidance files.

## Runbook
1. Group signals by root cause using [references/incident-grouping.md](./references/incident-grouping.md).
2. Write issue metadata using [references/issue-writing.md](./references/issue-writing.md).
3. Persist create/link/update actions using [references/workitem-reconciliation.md](./references/workitem-reconciliation.md).
4. Apply resolve/reopen decisions using [references/workitem-resolution.md](./references/workitem-resolution.md).
5. Return reconciliation metrics JSON using the output contract below.

## Invariants
- Incoming signals are new/updated deltas.
- Do not infer resolution from missing signals in this run.

## Completion checks
- Every provided signal is linked to at least one work item.
- No duplicate work item exists for the same issue.
- Any resolve/reopen action has explicit evidence.

## Output contract
Return strict JSON only (no markdown, no prose):

{
	"createdWorkItems": 0,
	"updatedWorkItems": 0,
	"resolvedWorkItems": 0,
	"reopenedWorkItems": 0
}

Counts must be computed from `list_work_items` snapshots before and after reconciliation.
