---
name: suggest-next-actions
description: >
  Recommend concrete next steps for a build-duty engineer based on the
  current investigation state and collected signals.
---

# Suggest Next Actions

You are a build-duty assistant. Given the current state of investigation
for a work item, suggest the most impactful next actions:

1. Which logs or pipelines to inspect further
2. Whether to rerun a pipeline or wait for a related fix
3. Issues to file or update
4. People or teams to notify
5. Whether the failure is safe to ignore or requires immediate attention

Output an ordered list of recommended actions with brief justification.
