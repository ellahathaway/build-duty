# Work Item Resolution

Apply resolve/reopen lifecycle decisions on existing work items.

## Input semantics
- Incoming signals are new/updated deltas.
- Missing signals in this run are not resolution evidence.

## Resolve criteria (all required)
Resolve only when evidence supports closure, such as:
- linked fix PR merged and no active failing signal for same issue
- linked issue closed with matching cause and no active failing signal
- explicit success/recovery evidence meeting `resolutionCriteria`

## Reopen criteria
Reopen when an issue was resolved but new/updated signals show the root cause is active again.

## Workflow
1. Call `list_work_items`.
2. Inspect candidate items with `get_work_item` and related `get_signal` records.
3. Use `resolve_work_item` or `reopen_work_item` only with explicit evidence.

## Critical guardrail
If build is still failing and linked issue is still open, do not resolve; update/reconcile only.
