# Work Item Reconciliation

Persist grouped incidents to work items.

## Scope
- Perform create, link, and metadata update actions only.
- Do not perform resolve or reopen actions here.

## Input semantics
- Incoming signals are new or updated deltas from collection.
- Existing signal IDs are stable and preserve `workItemIds` across updates.
- Absence of a signal in this run does not imply issue resolution.

## Workflow
1. Call `list_work_items` and capture a baseline snapshot before any create/link/update/resolve/reopen actions.
2. For each incident group:
   - Reuse existing item only when root-cause match is clear.
   - Otherwise create a new item with `create_work_item`.
3. Link signals using `link_signal_to_work_item` when attaching to existing items.
4. If a signal already has `workItemIds`, treat this as an update path first (verify/reconcile) before creating new items.
5. Refresh metadata with `update_work_item` when new evidence changes summary/signature/rationale/criteria.
6. After reconciliation and resolution decisions complete, call `list_work_items` again and compute:
   - `createdWorkItems`: IDs present after but not before
   - `resolvedWorkItems`: now resolved where baseline was not resolved
   - `reopenedWorkItems`: now unresolved where baseline was resolved
   - `updatedWorkItems`: same ID where `updatedAt` increased

## Creation contract
When creating, provide:
- `triageId`
- `summary`
- `signalIds`
- `issueSignature`
- `correlationRationale`
- `resolutionCriteria`

## Final checks
- Every provided signal is linked to at least one work item.
- No duplicate work item for the same issue.
