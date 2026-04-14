# Issue Writing

Produce issue metadata fields for new or updated work items.

## Required fields
- `summary`: concise statement of root cause and impact. Derive from the linked analyses' root cause and evidence.
- `issueSignature`: short stable causal signature (e.g., `nuget-restore-timeout-dnceng`, `openssl-sha1-alpine323`)

## Quality rules
- Focus on root cause, not symptom list.
- For causal chains, the work item summary should describe the root cause (upstream), not the downstream effects.
- Keep wording specific and actionable.
- Avoid vague labels like component names alone.
