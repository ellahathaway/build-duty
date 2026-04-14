# Incident Grouping

Determine which signals represent the same underlying issue.

## Input semantics
- The provided signal set is a delta stream of new or updated signals.
- Do not assume missing historical signals are resolved.
- Use each signal's `workItemIds` to understand existing linkage history.

## Workflow
1. For each signal ID in the triage run, load the signal and read: `Cause`, `Effect`, `Evidence`, `Context`, `WorkItemIds`, and relevant `Info` fields.
2. Use `Cause` and `Evidence` as primary correlation evidence.
3. Cross-reference `evidence` across signals to find causal chains (see below).
4. Use `context` to understand pipeline/repo dependencies and relationships.
5. Form groups only when causal evidence aligns.

## Correlation strategy

### Same-cause grouping
Merge signals when their analyses indicate the same failure mechanism. Compare using:
- **Cause text** — do the analyses describe the same failure?
- **Structural evidence** — do the analyses share pipeline definition IDs, failing task/stage names, file paths, error messages, or affected components? Shared structural evidence is strong correlation even when cause wording differs.

Examples:
- Two pipelines both failed with `error NU1301: Unable to load service index` → same NuGet infra issue.
- Three source-build phases failed with the same compiler error → same code issue.
- Two analyses reference the same file path and failing task but describe the symptom differently → same root cause.

### Causal chain detection
A downstream signal may fail **because** of an upstream signal's failure. Detect this by cross-referencing `evidence` fields:
- If signal A's evidence mentions a build number, pipeline name, or run ID that matches signal B's build metadata → signal A may be a downstream effect of signal B.
- If signal A's cause describes an artifact download failure and signal B is the pipeline that produces those artifacts → group them under signal B's root cause.
- If a GitHub PR's CI check failed and the failing pipeline matches another collected pipeline signal → link them.
- If a GitHub issue references a PR or pipeline that matches another signal → link them.

When a causal chain is detected, the **upstream** signal's cause becomes the work item's root cause. The downstream signal is a symptom.

### Cross-type correlation
Signals of different types (AzDo pipeline, GitHub issue, GitHub PR) can be the same incident:
- A GitHub issue tracking a build failure + the pipeline signal showing that failure → same incident.
- A GitHub PR fixing an issue + the issue signal → same incident.
- A pipeline failure + a downstream pipeline that failed because of it → same incident.

## Merge criteria
Merge only when causes indicate the same failure mechanism, OR when evidence shows a causal chain between signals.

## Do not merge on
- Same tests only
- Same component/repo/stage only
- Generic wording overlap
- Signals that happen to fail at the same time but for different reasons

## Precision rule
If uncertain, split. Only merge when you have clear causal evidence.
