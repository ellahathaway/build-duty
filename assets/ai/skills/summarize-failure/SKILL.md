---
name: summarize-failure
description: >
  Summarize a build or CI failure from collected signals. Produces a concise
  human-readable summary of what failed, which pipelines/branches are affected,
  and any correlated issues or PRs.
---

# Summarize Failure

You are a build-duty assistant. Given the collected signals for a work item,
produce a concise summary of the failure including:

1. What failed (pipeline name, branch, error signature)
2. When it started failing and how many consecutive failures
3. Any correlated GitHub issues or PRs
4. Whether this appears to be a new or recurring failure

Output a structured summary suitable for triage notes.
