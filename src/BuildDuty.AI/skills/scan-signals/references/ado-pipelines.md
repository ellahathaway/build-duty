# ADO Pipeline Signals

Signal type: `ado-pipeline-run`. Collected deterministically — the AI does not query ADO.

## Signal fields

| Field | Format |
|-------|--------|
| ID | `wi_ado_{buildId}` |
| Title | `[{pipelineName}] {branch} — Build #{buildNumber} {result}` |
| Correlation ID | `corr_ado_{pipelineId}_{sanitizedBranch}` |
| Signal type | `ado-pipeline-run` |
| Signal ref | `{orgUrl}/{project}/_build/results?buildId={buildId}` |

Branch sanitization: replace `/` and `\` with `_`.

## Triage rules

- `matchesFilter: "true"` + no existing work item → `create_work_item`
- Existing work item + latest signal shows `succeeded` → `resolve_work_item` with `"Auto-resolved: latest build succeeded"`
