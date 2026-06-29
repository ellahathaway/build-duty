---
name: generate-handoff
description: Generate a rotation handoff report summarizing active incidents, recent resolutions, and items needing attention for the next on-call engineer.
---

# Generate Handoff

Produce a structured handoff document for build-duty rotation transitions.

## Prerequisites

### MCP Servers
- The Build-Duty MCP server must be available
- The GitHub Copilot API MCP server must be available: HTTP, "https://api.githubcopilot.com/mcp/"
- The Azure DevOps MCP server must be available: HTTP, "https://mcp.dev.azure.com/"

## Steps

### 1. Collect current state

Use the `build-duty-mcp-server` to retrieve:
- Active incidents from the most recent triage run
- Signal trends over the past rotation period

### 2. Summarize active incidents

For each active incident:
- Current status and severity
- What has been done so far
- What needs attention next
- Links to tracking issues/PRs

### 3. Note recent resolutions

List incidents that were resolved during this rotation with a brief summary of what fixed them.

### 4. Flag items needing attention

Highlight anything that:
- Has been stale for more than a week
- Is escalating (more signals appearing)
- Requires coordination with another team

## Output

A handoff report suitable for posting in a Teams channel or issue comment.

If the user asks you to post the handoff as a comment on a GitHub issue or PR, follow the `post-comment` skill: render the exact comment in a fenced preview, get explicit confirmation, then post that exact string.

Render every reference to a build, pipeline, branch, pull request, or issue as a Markdown hyperlink, not bare text:
- AzDO build → the signal's `Url`
- AzDO pipeline → `{OrganizationUrl}/{ProjectName}/_build?definitionId={PipelineId}`
- Branch → `https://github.com/{owner}/{repo}/tree/{branch}` (owning repo)
- GitHub issue → `https://github.com/{owner}/{repo}/issues/{number}`
- GitHub PR → `https://github.com/{owner}/{repo}/pull/{number}`

If a branch's owning repo cannot be determined, use backticks instead of guessing a URL.
