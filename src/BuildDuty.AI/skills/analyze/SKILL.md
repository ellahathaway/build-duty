---
name: analyze
description: Analyze a signal and persist structured cause/effect/evidence for triage and correlation. Use when a single signal needs analysis.
---

# Analyze

Single-signal analysis — extract cause, effect, and evidence.

## References

Use the matching reference based on signal type:
- [references/azure-devops-pipeline.md](./references/azure-devops-pipeline.md)
- [references/github-pr.md](./references/github-pr.md)
- [references/github-issue.md](./references/github-issue.md)

## Workflow
1. Read the provided `signalId`.
2. Load signal data once using `get_signal(signalId)`.
3. Note the signal's `context` field (if present) — it describes what this signal source is and what it depends on.
4. Determine source type and use the matching reference document for instructions and evidence selection.
5. Extract three fields:
   - **Cause**: Why the signal is in its current state — the specific error, failure reason, or status reason.
   - **Effect**: What this means — impact on builds, releases, downstream consumers.
   - **Evidence**: Raw supporting details — error messages, stack traces, build numbers, issue/PR numbers, commit SHAs. Include anything that could link this signal to other signals.
6. Persist with `update_signal_analysis(signalId, cause, effect, evidence)`.

## Content requirements
- **Cause** should be specific and actionable (error codes, error messages, task names).
- **Effect** should describe real impact, not restate the cause.
- **Evidence** is not length-constrained. Include all relevant raw details. Always include any referenced build numbers, pipeline names, issue/PR URLs, and commit SHAs found in logs.
- If evidence is incomplete, state the strongest observable fact only.

## Speed constraints
- Do not call external services or MCP tools.
- Do not include markdown, bullets, or JSON in any field.

Do not return plain text as final output; persist via tool call.
