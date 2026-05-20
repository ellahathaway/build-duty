---
name: retry-build
description: Retry a failed Azure DevOps pipeline build after confirming the failure is transient (infrastructure issue, flaky test, timeout).
---

# Retry Build

Re-run a failed Azure DevOps pipeline build that was identified as a transient failure.

## Prerequisites

### MCP Servers
- The Build-Duty MCP server must be available
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

## Input

- Build URL or build ID
- Pipeline ID and project
- Confirmation that the failure is transient (from triage analysis)

## Steps

### 1. Queue a retry

Use the `azure-devops-mcp-server` to queue a new build with the same parameters.

### 2. Report

Provide the user with:
- Link to the new build
- Expected completion time
- What to check if it fails again

## Output

Confirmation that the build was retried, with a link to the new run.
