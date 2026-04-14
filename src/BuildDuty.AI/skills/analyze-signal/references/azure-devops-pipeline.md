# Reference: Azure DevOps Pipeline (SignalType.AzureDevOpsPipeline)

Applies when signal's type is an Azure DevOps pipeline. Pipeline details are in the signal's info.

---

## Classify the run outcome

Read the build result and monitored statuses in the signal's info:

- Result **in** `MonitoredStatuses` → [Non-successful pipeline](#non-successful-pipeline)
- Result **not in** `MonitoredStatuses` → [Resolved pipeline](#resolved-pipeline)

---

## Non-successful pipeline

### 1. Use timeline records

Use **only** the timeline records already present in the signal's `Info.TimelineRecords`. Do not fetch additional records from the build timeline — the records in the signal have been pre-filtered to the relevant scope. 

### 2. Review logs

For each failing record, read its most specific pipeline log. Move upward (task → job → stage) only if insufficient. Investigate each record independently — do not assume shared causes.

### 3. Create analyses

Persist the signal analysis for each distinct error. Consolidate only when multiple records clearly share one root cause.

#### Analysis Data — always include:

| Field | Source |
|---|---|
| `buildId` | `signal.Info.Build.Id` |
| `definitionId` | `signal.Info.Build.DefinitionId` |
| `sourceBranch` | `signal.Info.Build.SourceBranch` |
| `buildResult` | `signal.Info.Build.Result` |
| `monitoredStatuses` | `signal.Info.MonitoredStatuses` |
| `organizationUrl` | `signal.Info.OrganizationUrl` |
| `projectId` | `signal.Info.ProjectId` |
| `records` | Failing timeline record(s): `Name`, `RecordType`, `Result`, `Parents`, `relevantLogIds` |
| `logExcerpts` | Key error/warning text from logs |

Example:
```json
{
  "buildId": 12345,
  "definitionId": 42,
  "sourceBranch": "refs/heads/main",
  "buildResult": "Failed",
  "monitoredStatuses": ["Failed", "Canceled"],
  "organizationUrl": "https://dev.azure.com/dnceng",
  "projectId": "9ee6d478-d288-47f7-aacc-f6e6d082ae6d",
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

#### Analysis Text — concise cause statement

Examples:
- `NuGet restore timeout: error NU1301 unable to load service index for https://pkgs.dev.azure.com/`
- `Build failed with exit code 1: error CS0246 The type or namespace name Foo could not be found.`

If no concrete error is found, use the narrowest failure scope (e.g., "Task X failed") and note the error/warning text was not found.

---

## Resolved pipeline

Result is no longer in `MonitoredStatuses` (e.g. `Succeeded`, or `PartiallySucceeded` when only `Failed`/`Canceled` are monitored).

Resolve any existing active analyses on the signal — the failures they describe are no longer occurring. Use `resolve_signal_analysis` with a resolution reason describing the new build result.

#### Analysis Text — resolution reason

Example: `Pipeline result changed to Succeeded; no longer in monitored failure states [Failed, Canceled].`
