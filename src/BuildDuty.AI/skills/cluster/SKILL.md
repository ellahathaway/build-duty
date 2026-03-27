---
name: cluster
description: Cluster summarized signals into actionable groups before work-item updates.
---

# Cluster

Cluster summarized signals into groups that should map to work items.

## Source-specific references

Use signal metadata and summaries as the primary source of truth.

Use available tools to inspect existing work items before assigning groups.

## What to do

For the input signal summary list:

1. Read each signal entry (identity key, type, linked work-item IDs, summary).
2. Use summaries to cluster like signals that likely represent the same incident.
3. Prefer existing linked work-item IDs when present.
4. Keep unrelated summaries in separate clusters even if source type matches.
5. Return strict JSON only, no markdown.

## Output format

Return exactly this JSON shape:

{
  "clusters": [
    {
      "id": "cluster-identifier",
      "signalKeys": ["signal-key-1", "signal-key-2"]
    }
  ]
}

Rules:
- Every input signal key must appear exactly once across all clusters.
- Keep cluster IDs short and stable.
- Do not include extra properties.
