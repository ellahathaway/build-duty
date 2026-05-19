---
name: analyze-azure-devops-pipeline
description: Analyzes Azure DevOps pipeline build to determine root cause, impact, and context of build failures. Uses pipeline logs, timeline records, and build metadata.
---

# Analyze Azure DevOps Pipeline

Investigate Azure DevOps pipeline build to understand its status and what caused any failures/non-successful results.

# Prerequisites
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

## Context

The information given likely contains the following fields:
- `Url` — link to the build
- `OrganizationUrl` — the Azure DevOps organization (e.g., `https://dev.azure.com/dnceng`)
- `ProjectName` — the project (e.g., `internal`, `public`)
- `PipelineId` — the pipeline definition ID
- `Build` — build metadata: `Id`, `Result`, `DefinitionId`, `SourceBranch`, `FinishTime`
- `TimelineRecords` — failed/warning timeline records with `Name`, `RecordType` (Stage/Job/Task), `Result`, and `LogId`

## Investigate Pipeline Build

### 1. Understand the failure scope

Review `TimelineRecords` to understand what failed:
- Which **stages** failed or had issues?
- Which **jobs** within those stages failed?
- Which **tasks** within those jobs failed?

**IMPORTANT**: Only investigate the timeline records provided in the data. Do NOT list additional logs, fetch other timeline records, or explore logs outside of those referenced by the provided `LogId` values. The provided information already contains the relevant failure scope. Any failure or non-successful result outside of the provided timeline records is out of scope for this investigation.

### 2. Read failure logs

For each failed record that has a `LogId > 0`, **read the pipeline log** using the `azure-devops-mcp-server` MCP tools:
- Use `pipelines_build_log` with action `get_content` to read log content by log ID
- Only read logs for the `LogId` values present in the provided timeline records
- Do NOT use `pipelines_build_log` with action `list` to discover other logs
- Look for error messages, exception stack traces, exit codes
- Look for timeout indicators (e.g., "exceeded the timeout", "canceled due to timeout")
- Look for infrastructure errors (e.g., "agent lost communication", "disk full", "out of memory")
- Look for test failure summaries

### 3. Classify the failure

Determine the failure category:
- **Test failure** — one or more tests failed with assertions or crashes
- **Build failure** — compilation or packaging error
- **Timeout** — stage/job/task exceeded time limit
- **Infrastructure** — agent/machine issue, network failure, disk space, out of memory
- **Dependency** — upstream artifact unavailable, package restore failure
- **Configuration** — pipeline YAML error, missing variables, wrong parameters

## Output

Produce:
- **RootCause**: A concise description of why the build failed (e.g., "Test `BuildTests.SmokeTest` timed out after 90 minutes in stage `Build`", "Step `Build` failed due to missing environment variable `DOTNET_ROOT`")
- **Category**: One of: `TestFailure`, `BuildFailure`, `Timeout`, `Infrastructure`, `Dependency`, `Configuration`, `Unknown`
- **Context**: Any additional context — is this a known pattern? Does the error reference a specific PR or commit?
