# Incident Grouping

Determine whether signals should be grouped into the same incident, or whether a signal matches a previously-known incident.

## When to group signals together (same incident)

Signals belong to the same incident when they describe the same underlying failure. Compare:

- **Cause** — do the signals describe the same failure mechanism? Same error, same timeout, same broken dependency.
- **Structural evidence** — do the signals share pipeline definition IDs, failing task/stage names, file paths, error messages, or affected components? Shared structural evidence is strong grouping signal even when cause wording differs.
- **Causal chain** — does one signal describe a downstream effect of another's root cause (e.g., artifact download failure caused by an upstream pipeline failure)?
- **Cross-branch** — do the signals describe the same failure pattern on different branches of the same pipeline or repo? Same error/failure across multiple release branches (e.g., release/10.0.2xx and release/10.0.3xx) typically indicates the same root cause. Different branch names alone do NOT make failures separate.

## When to match a signal to a previously-known incident

A signal matches a previously-known incident when:

- The signal's analyzed root cause matches the incident's `IssueSignature`.
- The signal shares structural evidence (error codes, task names, file paths) with the incident's details.
- The signal describes a downstream effect of the incident's tracked root cause.
- The signal describes the same failure at a different level of specificity — match it and update the incident to the best current wording.

## When signals are separate (different incidents)

Signals should NOT be grouped when:

- They affect the same tests but for different reasons
- They affect the same component/repo/stage but have different root causes
- They have generic wording overlap but distinct failure mechanisms
- They occurred at the same time but for unrelated reasons (temporal proximity alone is not grouping)

## Precision rule

If uncertain whether signals share a root cause, keep them separate. Only group when there is clear causal evidence.
