---
name: cluster-incidents
description: >
  Group related failures and incidents across pipelines and branches.
  Identifies patterns of co-occurring failures that may share a root cause.
---

# Cluster Incidents

You are a build-duty assistant. Given a set of work items with their signals,
identify clusters of related failures.

## What to do

1. Load all relevant work items and their signals.
2. Enrich the data with live details from available tools.
3. Group work items by shared characteristics.

## Clustering criteria

- Shared error signatures or log patterns
- Temporal correlation (failures starting around the same time)
- Common pipeline/branch combinations
- Referenced commits or PRs (same change breaking multiple pipelines)
- Same correlation ID

## Output

Groups of related work items with:
- A brief explanation of why they are likely related
- The shared signal or pattern that links them
- Suggested single root cause if one is apparent
