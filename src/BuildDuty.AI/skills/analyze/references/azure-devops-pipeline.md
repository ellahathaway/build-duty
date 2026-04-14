# Reference: Azure DevOps Pipeline

Use this when the signal type is an azure devops pipeline.

## Context awareness

Check the signal's `context` field. It describes what this pipeline does, what depends on it, and other domain knowledge. Use it to understand the significance of failures.

### Run outcome classification

Treat the pipeline as **non-successful** when run result is not `Succeeded` (commonly includes `Failed`, `Canceled`, `PartiallySucceeded`, and sometimes `SucceededWithIssues`).

## Checklist

### Step 1 — Determine "what happened"
1. Identify the final run result.
1. If timeline records are available, identify the different pipeline timeline results.

### Step 2 — Retrieve and read logs
If timeline records are available, prefer their logs to the pipeline build log.

Escalation order for timeline logs:
1. Task/Step log (record's own `LogId`)
2. Job log (closest parent job record with a `LogId`)
3. Phase log (closest parent phase record with a `LogId`)
4. Stage log (closest parent stage record with a `LogId`)

Only escalate upward if the current level is missing (`LogId` null) or does not provide sufficient information on what happened.

**Drilling into child records**: When the signal's timeline records are at the Phase or Job level and their logs do not contain the specific failure cause (e.g., the log only shows job preparation/template expansion), use `get_pipeline_timeline_records(signalId, parentRecordId)` to list the child records under that Phase/Job. Look for child records with a non-success `Result` and a `LogId`, then fetch and read those logs. Repeat downward (Phase → Job → Task) until you find a log with the concrete error.

When multiple timeline records failed with the same cause, you only need to identify the cause from one representative record. Mention that all N records/phases failed for the same reason.

To retrieve the log, call `get_pipeline_log(signalId, logId)` and use the returned file path value.

For each log you decide to read:
1. Call `read_pipeline_log_chunk(logPath, chunkOffsetFromBottom, chunkSize)`.
2. Defaults:
   - `chunkOffsetFromBottom = 0`
   - `chunkSize = 100`
3. Determine why the log had the corresponding result type.
3. If inconclusive, increment offset: `1, 2, 3, ...` until:
   - a concrete cause is found, or
   - `HasMoreChunks = false`

- Do not use shell/bash tools (grep/sed/wc) for analysis.

### Step 3 — Extract cause, effect, and evidence

From the logs and timeline data, extract:

**Cause**: The specific error or failure reason. Include error codes, error messages, and the failing task/step name. Examples:
- `NuGet restore timeout: error NU1301 unable to load service index for https://pkgs.dev.azure.com/dnceng/...`
- `Build task failed with exit code 1 in SB_CentOSStream10_Online_MsftSdk_x64: 'error CS0246: The type or namespace name Foo could not be found'`
- `Artifact download failed: upstream build 20260405.1 in dotnet-unified-build did not produce artifacts`

**Effect**: The real-world impact. Examples:
- `All 9 source-build phases failed; no source-built SDK produced for commit 7cbb1fba`
- `SDK diff comparison blocked; cannot verify source-build parity for this release`

**Evidence**: Raw details for cross-signal correlation. Always include:
- Exact error messages or stack traces from logs
- Any build numbers, pipeline names, or run IDs mentioned in error text
- Commit SHAs, branch names
- Any referenced issue or PR numbers

No speculation. If you can't find a cause in the logs, set cause to the narrowest failure scope observed and note in evidence that the specific cause was not found.
