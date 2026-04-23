# Incident Grouping

Determine whether a new or updated signal analysis still correlates with the work item it is linked to.

## When to correlate

An analysis correlates with a work item when they describe the same underlying failure. Compare:

- **Cause** — does the analysis describe the same failure mechanism as the work item's `IssueSignature`?
- **Structural evidence** — does the analysis share pipeline definition IDs, failing task/stage names, file paths, error messages, or affected components with the work item? Shared structural evidence is strong correlation even when cause wording differs.
- **Causal chain** — does the analysis describe a downstream effect of the work item's root cause (e.g., artifact download failure caused by an upstream pipeline tracked by the work item)?
- **Cross-branch** — does the analysis describe the same failure pattern on a different branch of the same pipeline or repo? Same error/failure across multiple release branches (e.g., release/10.0.2xx and release/10.0.3xx) typically indicates the same root cause. Different branch names alone do NOT make failures separate.
- **Specificity drift** — does the analysis describe the same failure but at a different level of specificity (more precise or more generalized) than the current work item? If yes, keep the correlation and refresh work item metadata to the best current wording.

## When to unlink

Unlink when the analysis no longer describes the same failure as the work item:

- The analysis's root cause has changed to something unrelated to the work item's `IssueSignature`.
- The structural evidence (error codes, task names, file paths) no longer overlaps with the work item.
- The analysis was a symptom of a causal chain that no longer holds.

## Do not correlate on

- Same tests only
- Same component/repo/stage only
- Generic wording overlap
- Temporal proximity (failures at the same time but for different reasons)

## Precision rule

If uncertain, unlink. Only keep the link when there is clear causal evidence.
