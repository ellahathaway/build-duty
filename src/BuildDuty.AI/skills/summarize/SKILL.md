---
name: summarize
description: Summarize a signal using its payload and source context. Return a concise triage-ready summary.
---

# Summarize

Summarize a signal.

## Source-specific references

Use the matching reference based on the signal type.

Always ground the summary in source-specific details from these references.

## What to do

For the single input signal:

1. Read the provided signal payload carefully.
2. Determine the source type.
3. Use the corresponding reference document to extract the right details.
4. Return a concise 1-3 sentence summary that includes:
   - **What** happened (failure, timeout, review blocker, issue state)
   - **Where/Context** (pipeline stage/job/task, repo/PR, issue scope)

Return only the summary text.
