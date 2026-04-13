# Reference: Azure DevOps Pipeline

## When to use

Use for Azure DevOps pipeline run signals.

## Signal info

Use stored signal payload only (run metadata + timeline records when present).
Do not fetch external logs during summarization.

## Summary focus

- build result/status
- most relevant failing or warning timeline point (`Stage > Job > Task`) when available
- concrete cause text from existing issues/messages in the stored payload
- impact context (what is blocked) when obvious

## Keep it concise
- 1 sentence preferred, max 2.
- If cause is unknown, state the observed failing scope without speculation.
- No markdown or bullet formatting in final summary text.

