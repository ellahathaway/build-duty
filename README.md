# BuildDuty

A .NET CLI tool that streamlines build-duty workflows by centralizing signal
collection from Azure DevOps and GitHub, tracking work items through a clear
lifecycle, and enabling AI-assisted triage and scanning — all driven by
repository-owned YAML configuration.

## Why BuildDuty?

Build-duty engineers juggle multiple dashboards, pipelines, and issue trackers to
understand repository health. BuildDuty consolidates that work into a single,
auditable workflow:

- **AI-powered scanning** — scan Azure DevOps pipelines and GitHub issues/PRs
  using AI agents backed by MCP servers.
- **Work-item lifecycle** — track incidents from `Unresolved` → `InProgress` →
  `Resolved` with full history.
- **Auto-resolution** — AI agents automatically resolve work items when builds
  pass, release branches are superseded, or issues are closed.
- **Release branch discovery** — automatically discovers active .NET release
  branches from the dotnet/core releases index via a bundled Python script.
- **AI-assisted triage** — bundled skills summarize failures, cluster related
  incidents, diagnose root causes, and suggest next actions via the GitHub
  Copilot SDK.
- **Repo-owned configuration** — a `.build-duty.yml` file in your repository
  declares exactly what to monitor.

## Quick start

```bash
# Install the CLI as a .NET global tool
dotnet tool install -g BuildDuty

# Scan configured pipelines and issues
build-duty scan

# List open work items
build-duty workitems list

# AI-assisted triage
build-duty triage --id wi_ado_12345 --action "summarize this failure"
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (see `global.json`)

## Building from source

### Linux / macOS

```bash
./eng/build.sh          # restore → build → test
./eng/build.sh --pack   # … + pack as NuGet
```

### Windows

```powershell
.\eng\build.ps1          # restore → build → test
.\eng\build.ps1 -Pack    # … + pack as NuGet
```

Or with the `dotnet` CLI directly:

```bash
dotnet restore BuildDuty.slnx
dotnet build   BuildDuty.slnx -c Release
dotnet test    BuildDuty.slnx -c Release
```

## Configuration

Place a `.build-duty.yml` file in your repository root. The `name` field is
required and determines the local storage directory (`~/.build-duty/<name>/`),
keeping data from different configs isolated.

```yaml
name: sourcebuild-monitor

azureDevOps:
  organizations:
    - url: https://dev.azure.com/dnceng
      projects:
        - name: internal
          pipelines:
            - id: 1234
              name: dotnet-source-build
              branches:
                - main
              release:
                repository: dotnet-dotnet
                supportPhases: [active, maintenance, preview]
                minVersion: 8
              status: [failed, partiallySucceeded]

github:
  repositories:
    - owner: dotnet
      name: source-build
      issues:
        labels: ["Build Break"]
        state: open
```

### Release branch auto-discovery

When a pipeline includes a `release` section, BuildDuty's scanning AI agent
calls a bundled Python script (`resolve-release-branches.py`) to discover
active .NET release branches. The script:

1. Queries the [dotnet/core releases index](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json)
   for supported channels matching the configured support phases and minimum version.
2. Downloads per-channel release data to detect shipped SDK versions and
   previews/RCs.
3. Returns structured JSON with supported channels, released SDK versions, and
   released preview identifiers.

The AI agent then uses MCP server tools to list branches in the configured
repository, matches them against the supported channels, and filters out
branches for released previews and superseded versions.

## CLI commands

### `build-duty scan`

Run AI-assisted scanning for configured sources, creating work items
for failures and auto-resolving work items when builds pass or branches are
superseded.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |

### `build-duty workitems list`

List tracked work items. Resolved items are hidden by default.

| Option | Description |
|---|---|
| `--state <state>` | Filter: `unresolved`, `inprogress`, `resolved` |
| `--show-resolved` | Include resolved work items in output |
| `--limit <n>` | Max items to display |

### `build-duty workitems show`

Show full details for a single work item, including signals and history.

| Option | Description |
|---|---|
| `--id <id>` | Work item ID (required) |

### `build-duty triage`

Run AI-assisted triage against one or more work items using the GitHub Copilot
SDK with bundled skills and MCP server integration.

| Option | Description |
|---|---|
| `--id <id>` | Single work item ID |
| `--action <text>` | AI action to perform (required) |
| `--state <state>` | Batch: select items in this state |
| `--show-resolved` | Batch: include resolved items |
| `--limit <n>` | Batch: max items to process |
| `--config <path>` | Path to config file (default: auto-detect) |

**Bundled skills:**

| Skill | Purpose |
|---|---|
| `summarize` | Concise failure summary with impact and next steps |
| `diagnose-build-break` | Root-cause analysis with ranked likely causes |
| `cluster-incidents` | Group related failures across pipelines/branches |
| `suggest-next-actions` | Recommend concrete next steps |
| `scan-signals` | AI-powered signal scanning (used by `build-duty scan`) |

## Architecture

```
BuildDuty.Cli          CLI entry-point, commands, rendering (Spectre.Console)
BuildDuty.Core         Domain model, work-item store, config
BuildDuty.AI           Copilot SDK adapter, skills, tools
BuildDuty.Tests        xUnit tests
```

### Data flow

```
build-duty scan
  ├─ CopilotAdapter     → Copilot SDK sessions with scan-signals skill
  │   ├─ ADO agent       → scans pipelines via Azure DevOps MCP server
  │   ├─ Issues agent    → scans GitHub issues via GitHub MCP server
  │   └─ PRs agent       → scans GitHub PRs via GitHub MCP server
  ├─ ScanTools           → create_work_item, resolve_work_item, list_work_items
  │   └─ get_release_branches → bundled Python script
  └─ WorkItemStore       ←── new / updated / resolved work items

build-duty triage
  ├─ CopilotAdapter     → Copilot SDK session with triage skills + MCP servers
  │   ├─ Skills          (summarize, diagnose, cluster, suggest)
  │   ├─ MCP: Azure DevOps  (pipeline details, timelines, logs)
  │   └─ MCP: GitHub         (issues, PRs, commits)
  ├─ BuildDutyTools      → work item data access for AI
  └─ TriageStore      ←── persisted result
```

### Local storage

Data is stored under `~/.build-duty/<name>/`, where `<name>` comes from the
`name` field in `.build-duty.yml`.

```
~/.build-duty/
└── sourcebuild-monitor/     ← matches name: sourcebuild-monitor
    ├── workitems/            # Work-item JSON files
    └── triage-runs/          # Triage run-result JSON files
```

Triage results are stored alongside work-item data for incremental analysis.

## Contributing

1. Fork and clone the repository.
2. Run `dotnet tool restore` to install local tooling.
3. Use `./eng/build.sh` (or `.\eng\build.ps1`) to build and test.
4. Open a pull request.

## License

See [LICENSE](LICENSE) for details.
