---
name: summarize
description: Summarize a signal using its payload and source context, then persist the summary with tools.
---

# Summarize

Summarize a signal.

## Source-specific references

Use the matching reference based on the signal type.

Always ground the summary in source-specific details from these references.

## What to do

For the single input signal:

1. Read the provided `signalId`.
2. Load the full signal payload with `get_signal_info(signalId)`.
3. Determine the source type.
4. Use the corresponding reference document to extract the right details.
5. Write a concise 1-3 sentence summary that includes:
   - **What** happened (failure, timeout, review blocker, issue state)
   - **Where/Context** (pipeline stage/job/task, repo/PR, issue scope)
6. Persist the summary by calling `update_signal_summary(signalId, summary)`.

Do not return the summary as plain text; use the tool to persist it.
