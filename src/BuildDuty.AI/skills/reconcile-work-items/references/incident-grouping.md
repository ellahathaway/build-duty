# Incident Grouping

Determine which signals represent the same underlying issue.

## Input semantics
- The provided signal set is a delta stream of new or updated signals.
- Do not assume missing historical signals are resolved.
- Use each signal's `workItemIds` to understand existing linkage history.

## Workflow
1. For each signal ID in the triage run, call `select_signal_fields` and request only required fields.
2. Use selected `summary`, `workItemIds`, and any info build fields as primary evidence.
3. Form groups only when causal evidence aligns.

## Merge criteria
Merge only when summaries indicate the same failure mechanism/cause.

## Do not merge on
- same tests only
- same component/repo/stage only
- generic wording overlap

## Precision rule
If uncertain, split.
