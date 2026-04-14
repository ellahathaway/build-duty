---
name: summarize
description: Create and persist a concise signal summary from stored signal data. Use when a single signal needs fast triage context.
---

# Summarize

Fast-path single-signal summarization.

## References

Use the matching reference based on signal type:
- [references/azure-devops-pipeline.md](./references/azure-devops-pipeline.md)
- [references/github-pr.md](./references/github-pr.md)
- [references/github-issue.md](./references/github-issue.md)

## Workflow
1. Read the provided `signalId`.
2. Load signal data once using `get_signal(signalId)`.
3. Determine source type and apply the matching reference.
4. Write a concise summary (1 sentence preferred, max 2).
5. Persist with `update_signal_summary(signalId, summary)`.

## Speed constraints
- Do not call external services or MCP tools.
- Do not perform additional lookups beyond `get_signal`.
- Do not include markdown, bullets, or JSON in the summary.
- Prefer <= 220 characters unless critical details require more.

## Content requirements
- Include what happened and where it happened.
- Include the most actionable blocker/cause when present.
- If evidence is incomplete, state the strongest observable fact only.

Do not return plain text as final output; persist via tool call.
