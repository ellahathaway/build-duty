---
name: scan-signals
description: >
  Triage pre-collected signals. Create and resolve build-duty work items
  based on the collected data provided in the prompt.
---

# Scan Signals — Triage

You receive pre-collected signals (JSON). Use `list_work_items` and
`work_item_exists` to check existing items before creating or resolving.

## Tools

- `create_work_item(id, title, correlationId, signalType, signalRef)` — create a tracked item
- `resolve_work_item(id, reason)` — resolve an existing item (sets status to "resolved")
- `work_item_exists(id)` — check if a work item already exists
- `list_work_items(status?, limit?)` — list existing work items (status: "resolved" or "unresolved")

## Workflow

1. Call `list_work_items` to get current tracked items.
2. For signals where `matchesFilter` is `"true"` and no work item exists: `create_work_item`.
3. For existing work items whose correlation ID matches a now-successful signal: `resolve_work_item`.
4. Output a brief summary (created, resolved, skipped).

## Rules

- Check `work_item_exists` before creating — no duplicates.
- Use the signal's `id`, `correlationId`, and `signalRef` fields directly.
- Do NOT query external services — all signal data is in the prompt.
