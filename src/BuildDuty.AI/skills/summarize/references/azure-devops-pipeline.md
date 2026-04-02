# Reference: Azure DevOps Pipeline

Use this when the signal type is an azure devops pipeline.

## Output (strict)
Final output must be plain text:
- Prefer 1 sentence; max 2 sentences.
- No markdown, bullets, or formatting.
- No speculation. If you can’t find a cause, say so and report the narrowest failure scope you observed.

Use this template style:
- Non-successful (cause found): `Run <result> in <Stage > Job > Task> failed because <concrete cause>.`
- Non-successful (cause unknown): `Run <result> in <Stage > Job > Task> failed; cause not found in available logs.`
- Successful: `Run succeeded; timeline execution is consistent with metadata (no failed records).`

### Run outcome classification

Treat the pipeline as **non-successful** when run result is not `Succeeded` (commonly includes `Failed`, `Canceled`, `PartiallySucceeded`, and sometimes `SucceededWithIssues`).

## Checklist

### Step 1 — Determine “what happened”
1. Identify the final run result.
1. If timeline records are available, identify the different pipeline timeline results.

### Step 2 — Retrieve and read logs
If timeline records are available, prefer their logs to the pipeline build log.

Escalation order for timeline logs:
1. Task/Step log (record’s own `LogId`)
2. Job log (closest parent job record with a `LogId`)
3. Phase log (closet parent phase record with a `LogId`)
4. Stage log (closest parent stage record with a `LogId`)

Only escalate upward if the current level is missing (`LogId` null) or does not provide sufficient information on what happened.

To retrieve the log, call `get_pipeline_log(signalId, logId)` and use the returned file path value.

For each log you decide to read:
1. Call `read_pipeline_log_chunk(logPath, chunkOffsetFromBottom, chunkSize)`.
2. Defaults:
   - `chunkOffsetFromBottom = 0`
   - `chunkSize = 100`
3. Determine why the log had the corresponding result type.
3. If inconclusive, increment offset: `1, 2, 3, ...` until:
   - a concrete cause is found, or
   - `HasMoreChunks = false`, or

- Do not use shell/bash tools (grep/sed/wc) for analysis.

## Step 3 - Output

- Give a summary of what happened and why based on the earlier output constraints.
