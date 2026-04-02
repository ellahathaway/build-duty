# Reference: Azure DevOps Pipeline

## When to use

Use for Azure DevOps pipeline run signals.

## Goal

Summarize both:
- what happened in the pipeline run
- why it happened (best supported cause)

## Signal info

Use stored signal payload (run metadata + timeline records) to determine the what.
Use logs to determine the why, in this order:
- `get_timeline_record_logs(signalId)` first
- `get_build_logs(signalId)` only when timeline logs are unavailable or empty

## Summary focus

- what failed (result/status + failing scope, e.g. `Stage > Job > Task`)
- why it failed (specific error/cause from timeline/build logs)
- impact when obvious (what is blocked)

## Keep it concise
- 1 sentence preferred, max 2.
- If why is unknown, state only the observed outcome scope without speculation.
- No markdown or bullet formatting in final summary text.
