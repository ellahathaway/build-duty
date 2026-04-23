# Reference: Azure DevOps Pipeline (SignalType.AzureDevOpsPipeline)

Applies when signal's type is an Azure DevOps pipeline. Pipeline details are in `signal.Info` (`JsonElement`).

---

## Analyze Signal

Applies to signals that were collected as `New` and `Updated`. The signal's `Info.TimelineRecords` contains pre-filtered failing timeline records.

### If `collectionReason` is `New`

- Analyze each failing timeline record and create analyses for each distinct root cause found.

### If `collectionReason` is `Updated`

- Re-analyze the latest `Info.TimelineRecords` and logs for each record.
- For each existing matching analysis, update the analysis text and data to reflect the newest evidence, even if the root cause category is unchanged.
- Resolve analyses that are no longer supported by current records/logs.
- Create analyses for newly introduced independent failures.

### Timeline records

- Use only the timeline records already present in the signal's `Info.TimelineRecords`.
- Do not fetch additional records from the build timeline — the records in the signal have been pre-filtered to the relevant scope.

### Reviewing logs

To read pipeline logs, express your intent to analyze Azure DevOps pipeline logs. The pipeline_log agent has tools to read azure devops pipeline logs and will be automatically selected to handle log retrieval.

For each timeline record, read its most specific pipeline log. Move upward (task → job → stage) only if insufficient. Investigate each record independently — do not assume shared causes.

Each distinct error/warning should be treated as a separate root cause unless multiple records clearly share one.

### Analysis data — always include:

| Field | Source |
|---|---|
| `records` | Failing timeline record(s): `Name`, `RecordType`, `Result`, `Parents`, `relevantLogIds` |
| `logExcerpts` | Key error/warning text from logs |

Example:
```json
{
  "records": [{
    "name": "Restore",
    "recordType": "Task",
    "result": "Failed",
    "parents": [
      { "name": "Build", "type": "Stage" },
      { "name": "Build", "type": "Job" }
    ],
    "relevantLogIds": [128, 10]
  }],
  "logExcerpts": [
    "error NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json"
  ]
}
```

### Analysis text — concise cause statement

Examples:
- `NuGet restore timeout: error NU1301 unable to load service index for https://pkgs.dev.azure.com/`
- `Build failed with exit code 1: error CS0246 The type or namespace name Foo could not be found.`

For `Updated` signals, rewrite the analysis text to match current logs/records rather than reusing stale prior-run wording.

If no concrete error is found, use the narrowest failure scope (e.g., "Task X failed") and note the error/warning text was not found.
