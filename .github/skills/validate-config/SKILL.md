---
name: validate-config
description: Validate a build-duty configuration file — checks syntax, verifies pipeline IDs exist, and confirms GitHub repo/label references are valid.
---

# Validate Config

Validate a build-duty configuration file for correctness.

## Prerequisites

### MCP Servers
- The Build-Duty MCP server must be available
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

## Input

- Path to a build-duty config file (or the file content)

## Steps

### 1. Parse and check syntax

Use the `build-duty-mcp-server` `build_duty_get_config` tool to parse the config. Report any YAML syntax errors or missing required fields.

### 2. Verify Azure DevOps references

For each pipeline referenced:
- Confirm the organization URL is reachable
- Confirm the project exists
- Confirm the pipeline ID exists and is active

### 3. Verify GitHub references

For each GitHub repo referenced:
- Confirm the organization/repo exists
- Confirm referenced labels exist on the repo
- Warn about any filters that would match zero items

### 4. Check for common issues

- Duplicate pipeline IDs
- Overlapping regex patterns that might double-count signals
- Missing `context` fields (optional but recommended)
- Release branch config without matching pipeline support

## Output

A validation report:
- ✅ Valid items
- ⚠️ Warnings (non-blocking but worth fixing)
- ❌ Errors (will cause runtime failures)
