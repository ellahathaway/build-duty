# BuildDuty

A .NET CLI tool that streamlines build-duty workflows by centralizing signal
collection from Azure DevOps and GitHub, tracking work items through a clear
lifecycle, and enabling AI-assisted triage — all driven by repository-owned
YAML configuration.

## Why BuildDuty?

Build-duty engineers juggle multiple dashboards, pipelines, and issue trackers to
understand repository health. BuildDuty consolidates that work into a single,
auditable workflow:

- **Deterministic collection** — collects pipeline runs, GitHub issues, and PRs
  into local work items. Failed tasks (stages, jobs, log IDs) are captured
  automatically. Passing builds auto-resolve their corresponding work items.
- **AI-powered summarize & triage** — bundled skills summarize failures from
  build logs, determine statuses, cross-reference related items, and suggest
  next actions via the GitHub Copilot SDK.
- **Correlation confirmation** — after triage, new cross-references are
  presented for human confirmation. Rejected correlations are saved as
  feedback so the AI learns from past mistakes.
- **Interactive review** — select work items from a grouped table and give
  freeform instructions to an AI agent (resolve, investigate, re-triage, etc.).
- **Work-item lifecycle** — track incidents from `new` through type-specific
  statuses (`tracked`, `needs-review`, `investigating`, etc.) to terminal
  states (`fixed`, `merged`, `resolved`, `closed`) with full history.
- **Release branch discovery** — automatically discovers active .NET release
  branches from the dotnet/core releases index via a bundled Python script.
- **Repo-owned configuration** — a `.build-duty.yml` file in your repository
  declares exactly what to monitor.

## Quick start

```bash
# Install the CLI as a .NET global tool
dotnet tool install -g BuildDuty

# Run the full triage pipeline (collect → summarize → triage)
build-duty triage

# Run triage and enter interactive review when done
build-duty triage --review

# Review existing work items interactively
build-duty review

# List open work items
build-duty workitems list

# Run an AI action against a specific work item
build-duty workitems run --id wi_ado_12345 --action "summarize this failure"
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
              stages:              # optional: only collect matching stages/jobs
                - "Build"
              legs:
                - "Build Linux*"

github:
  repositories:
    - owner: dotnet
      name: source-build
      issues:
        labels: ["Build Break"]
        state: open
      pullRequests:
        - namePattern: "dotnet-*"
          labels: ["auto-merge"]
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

### `build-duty triage`

Run the full triage pipeline: collect signals and create work items, summarize
new items with AI, then triage with AI to determine statuses and cross-references.
After triage, new correlations (links between items) are presented for
confirmation. Optionally enter interactive review with `--review`.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--review` | Enter interactive review mode after triage |

### `build-duty review`

Interactively review and act on triaged work items. Items are displayed in a
grouped table (by type and status). Select items, type a freeform instruction,
and an AI agent executes it.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |

### `build-duty workitems list`

List tracked work items. Resolved items are hidden by default.

| Option | Description |
|---|---|
| `--status <status>` | Filter: `unresolved` or `resolved` |
| `--show-resolved` | Include resolved work items in output |
| `--limit <n>` | Max items to display |

### `build-duty workitems show`

Show full details for a single work item, including signals, summary, and history.

| Option | Description |
|---|---|
| `--id <id>` | Work item ID (required) |

### `build-duty workitems run`

Run an AI action against one or more work items using the GitHub Copilot
SDK with bundled skills and MCP server integration.

| Option | Description |
|---|---|
| `--id <id>` | Single work item ID |
| `--action <text>` | AI action to perform (required) |
| `--status <status>` | Batch: select items by status |
| `--show-resolved` | Batch: include resolved items |
| `--limit <n>` | Batch: max items to process |
| `--config <path>` | Path to config file (default: auto-detect) |

**Bundled skills:**

| Skill | Purpose |
|---|---|
| `summarize` | Fetch build logs, write concise work-item summaries |
| `triage-signals` | Determine statuses, resolve stale items, cross-reference |
| `diagnose-build-break` | Root-cause analysis with ranked likely causes |
| `cluster-incidents` | Group related failures across pipelines/branches |
| `suggest-next-actions` | Recommend concrete next steps |

## Architecture

```
BuildDuty.Cli          CLI entry-point, commands, rendering (Spectre.Console)
BuildDuty.Core         Domain model, work-item store, signal collectors, config
BuildDuty.AI           Copilot SDK adapter, skills, tools
BuildDuty.Tests        xUnit tests
```

### Data flow

```
build-duty triage
  ├─ Step 1: Signal Collection  (deterministic, no AI)
  │   ├─ AzureDevOpsSignalCollector  → ADO pipeline runs + failure details
  │   ├─ GitHubIssueCollector        → GitHub issues
  │   └─ GitHubPrCollector           → GitHub PRs
  │   └─ WorkItemStore  ←── work items created, passing builds auto-resolved
  │
  ├─ Step 2: AI Summarize  (summarize skill)
  │   ├─ CopilotAdapter       → Copilot SDK session
  │   ├─ SummarizeTools        → set_summary, get_task_log
  │   │   └─ AzureDevOpsBuildClient → deterministic timeline/log fetching
  │   └─ WorkItemStore         ←── summaries written
  │
  ├─ Step 3: AI Triage  (triage-signals skill)
  │   ├─ CopilotAdapter       → Copilot SDK session + MCP servers
  │   │   ├─ gh CLI / MCP     → GitHub issue/PR details
  │   │   └─ az CLI / MCP     → ADO pipeline details
  │   ├─ SignalTriageTools     → resolve, status, links
  │   └─ WorkItemStore         ←── statuses updated, items linked/resolved
  │
  ├─ Correlation Confirmation  (human-in-the-loop)
  │   └─ Confirm/reject new links; feedback saved for AI learning
  │
  └─ Interactive Review  (optional, --review flag)
      └─ Select items → freeform instruction → AI agent executes

build-duty review
  ├─ Grouped item table (type × status)
  ├─ Multi-select → freeform instruction
  ├─ CopilotAdapter  → agent with triage/diagnose/suggest skills
  └─ Loop until done

build-duty workitems run
  ├─ CopilotAdapter     → Copilot SDK session with all skills + MCP servers
  ├─ BuildDutyTools      → work item data access for AI
  └─ WorkItemStore       ←── persisted result
```

### Local storage

Data is stored under `~/.build-duty/<name>/`, where `<name>` comes from the
`name` field in `.build-duty.yml`.

```
~/.build-duty/
└── sourcebuild-monitor/          ← matches name: sourcebuild-monitor
    ├── workitems/                 # Work-item JSON files
    ├── triage-runs/               # Triage run-result JSON files
    └── triage-feedback.jsonl      # Rejected correlation feedback
```

## Contributing

1. Fork and clone the repository.
2. Run `dotnet tool restore` to install local tooling.
3. Use `./eng/build.sh` (or `.\eng\build.ps1`) to build and test.
4. Open a pull request.

## License

See [LICENSE](LICENSE) for details.
