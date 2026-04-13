# Issue Writing

Produce issue metadata fields for new or updated work items.

## Required fields
- `summary`: concise statement of root cause and impact. Derive from signal `cause` and `effect` fields.
- `issueSignature`: short stable causal signature (e.g., `nuget-restore-timeout-dnceng`, `openssl-sha1-alpine323`)
- `correlationRationale`: explicit why grouped signals share one cause. Reference specific signal IDs and their `cause`/`evidence` fields. For causal chains, explain the upstream→downstream relationship.
- `resolutionCriteria`: concrete condition to mark resolved

## Quality rules
- Focus on root cause, not symptom list.
- For causal chains, the work item summary should describe the root cause (upstream), not the downstream effects.
- Keep wording specific and actionable.
- Avoid vague labels like component names alone.
