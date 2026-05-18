# BuildDuty

A .NET toolkit for build-duty workflows — collects signals from Azure DevOps
and GitHub, and provides Copilot skills and an MCP server for AI-powered triage.

## Architecture

BuildDuty is split into deterministic libraries (signal collection, config) and
AI integration (MCP server, Copilot skills):

```
┌─────────────────────────────────────────────────────────────┐
│  Copilot Surface (CLI, VS Code, Workspace)           │
│                                                             │
│  /triage, /analyze-*, /reconcile skills │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐   │
│  │              MCP Tool Calls                          │   │
│  └────────┬───────────────────┬───────────────────┬─────┘   │
└───────────┼───────────────────┼───────────────────┼─────────┘
            │                   │                   │
   ┌────────▼─────────┐ ┌──────▼────────┐ ┌───────▼─────────┐
   │ BuildDuty MCP     │ │ AzDO MCP      │ │ GitHub MCP      │
   │ (build-duty-mcp)  │ │ Server        │ │ Server          │
   │                   │ │               │ │                 │
   │ - collect signals │ │ - logs        │ │ - issues        │
   │ - read config     │ │ - builds      │ │ - PRs           │
   └─────────┬─────────┘ └───────────────┘ └─────────────────┘
             │
   ┌─────────▼─────────────────────────────────┐
   │ BuildDuty Libraries (no AI)               │
   │  BuildDuty.Configuration + BuildDuty.Signals │
   └───────────────────────────────────────────┘
```

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (see `global.json`)
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az`) — for Azure DevOps access
- [GitHub CLI](https://cli.github.com/) (`gh`) — for GitHub access

### Authenticate

```bash
az login
gh auth login
```

### Build and install

```bash
git clone https://github.com/ellahathaway/build-duty.git
cd build-duty

# Build, test, pack, and install the MCP server
./eng/build.sh --install

# Verify
build-duty-mcp --help
```

## Usage

### Option 1: Triage Skill (recommended)

Clone this repo and using the `/triage` skill to triage + explore active incidents.

```
@build-duty triage my pipelines
@build-duty investigate the timeout in dotnet-source-build
```

The agent uses the skills in `.github/copilot/skills/` and connects to three
MCP servers (BuildDuty, Azure DevOps, GitHub) configured in `.github/copilot/mcp.json`.

### Option 2: MSBuild Task

Reference the `BuildDuty.Tasks` package for deterministic signal collection:

```xml
<Project>
  <UsingTask TaskName="BuildDuty.Tasks.CollectSignals"
             AssemblyFile="path/to/BuildDuty.Tasks.dll" />

  <Target Name="CollectBuildDutySignals">
    <CollectSignals ConfigPath=".build-duty.yml"
                    OutputPath="$(ArtifactsDir)/signals.xml" />
  </Target>
</Project>
```

### Option 3: MCP Server

Install and configure the MCP server:

```json
{
  "mcpServers": {
    "build-duty-mcp-server": {
      "command": "dotnet",
      "args": ["dnx", "--yes", "ellahathaway.buildduty.mcp"]
    }
  }
}
```

Available tools:
- `build_duty_collect_signals` — collect signals from configured sources
- `build_duty_get_config` — read and resolve a `.build-duty.yml` config

## Configuration

Create a `.build-duty.yml`:

```yaml
name: my-repo-monitor

azureDevOps:
  organizations:
    - url: https://dev.azure.com/dnceng
      projects:
        - name: internal
          pipelines:
            - id: 1234
              name: my-pipeline
              branches:
                - main

github:
  organizations:
    - name: dotnet
      repositories:
        - name: my-repo
          issues:
            - name: ".*"
              labels:
                - "Build Break"
              state: open
```

See the [configs/](configs/) directory for full examples.

### GitHub Issue and PR fields

Each entry under `issues` or `prs` is a pattern. All fields except `name` are optional.

| Field | Type | Description |
|---|---|---|
| `name` | regex | Title regex — only items whose title matches are included |
| `state` | `open` \| `closed` \| `all` | State filter (default: `open`) |
| `authors` | list | Allowlist of login names. Use `app/<name>` for GitHub Apps |
| `labels` | list | Include only items with **all** of these labels (AND) |
| `excludeLabels` | list | Exclude items with **any** of these labels (OR) |
| `context` | string | Free-text context injected into AI analysis prompts |

### Release branch auto-discovery

When a pipeline includes a `release` section, active .NET release branches are
automatically discovered from the [dotnet/core releases index](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json).

## Building from source

### Linux / macOS

```bash
./eng/build.sh              # restore → build → test
./eng/build.sh --pack       # … + pack as NuGet
./eng/build.sh --install    # … + pack + install MCP server
```

### Windows

```powershell
.\eng\build.ps1              # restore → build → test
.\eng\build.ps1 -Pack        # … + pack as NuGet
.\eng\build.ps1 -Install     # … + pack + install MCP server
```

Or with the `dotnet` CLI directly:

```bash
dotnet restore BuildDuty.slnx
dotnet build   BuildDuty.slnx -c Release
dotnet test    BuildDuty.slnx -c Release
```

## Copilot Skills

Skills are in `.github/skills/` and available to anyone who clones the repo:

| Skill | Description |
|-------|-------------|
| `/triage` | Full workflow — collect, analyze, reconcile |
| `/analyze-azure-devops-pipeline` | Investigate a pipeline failure |
| `/analyze-github-issue` | Investigate a GitHub issue |
| `/analyze-github-pull-request` | Investigate a PR |
| `/reconcile-findings` | Group and deduplicate findings |
| `/review-work-items` | Deep-dive into specific incidents |

## Contributing

1. Fork and clone the repository.
2. Use `./eng/build.sh` (or `.\eng\build.ps1`) to build and test.
3. Open a pull request.
