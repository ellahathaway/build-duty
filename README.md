# BuildDuty

A .NET CLI tool that streamlines build-duty workflows by centralizing work item
collection from Azure DevOps and GitHub, tracking work items through a clear
lifecycle, and enabling AI-assisted triage — all driven by repository-owned
YAML configuration.

## Why BuildDuty?

Build-duty engineers juggle multiple dashboards, pipelines, and issue trackers to
understand repository health. BuildDuty consolidates that work into a single,
auditable workflow:

- **Deterministic collection** — collects pipeline runs, GitHub issues, and PRs
  into local work items. Failed tasks (stages, jobs, log IDs) are captured
  automatically. Passing builds mark work items as `closed` state for triage.
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

## Quick start (from a local clone)

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (see `global.json`)
- [GitHub Copilot in the CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) —
  the Copilot SDK needs the `copilot` binary on your PATH
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az`) —
  required for Azure DevOps pipeline access
- [GitHub CLI](https://cli.github.com/) (`gh`) — required for GitHub issue/PR access
- [Node.js / npm](https://nodejs.org/) — the Azure DevOps MCP server is fetched via `npx`

### 2. Authenticate

```bash
# Azure DevOps (needed for pipeline data)
az login

# GitHub (needed for issues, PRs, and Copilot SDK)
gh auth login
```

### 3. Build and install locally

```bash
# Clone the repo
git clone https://github.com/ellahathaway/build-duty.git
cd build-duty

# Build and test
./eng/build.sh

# Pack and install as a global tool
./eng/build.sh --pack
dotnet tool install --global --add-source artifacts/packages buildduty

# Verify
build-duty --help
```

### 4. Create a config file

Create a `.build-duty.yml` in the repository you want to monitor (or anywhere —
you can pass `--config <path>` to any command):

```yaml
name: my-repo-monitor          # required — drives local storage isolation

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
              status: [failed, partiallySucceeded, canceled]

github:
  organizations:
    - organization: dotnet
      repositories:
        - name: my-repo
          issues:
            labels: ["Build Break"]
            state: open
```

### 5. Run the triage pipeline

```bash
# Full pipeline: collect → summarize → triage → review
build-duty triage --review

# Or step by step:
build-duty triage              # collect + summarize + triage
build-duty review              # interactive review of triaged items
build-duty workitems list      # see current work items
```

### Updating after code changes

After making changes to the source, rebuild and reinstall:

```bash
./eng/build.sh --pack
dotnet tool uninstall -g buildduty
dotnet tool install --global --add-source artifacts/packages buildduty
```

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
              release:                              # auto-discover release branches
                repository: dotnet-dotnet
                supportPhases: [active, maintenance, preview]
                minVersion: 8
              status: [failed, partiallySucceeded, canceled]
              age: 7d                               # only runs from the last 7 days
              stages:                               # optional: filter to specific stages/jobs
                - name: "Build"
                  jobs:
                    - "Build Linux*"

github:
  organizations:
    - organization: dotnet
      repositories:
        - name: source-build
          issues:
            labels: ["Build Break"]
            state: open
          prs:
            - name: "dotnet-*"
```

### Release branch auto-discovery

When a pipeline includes a `release` section, the `ReleaseBranchResolver`
service automatically discovers active .NET release branches. The resolver:

1. Looks up the pipeline's Git repository via `az pipelines show`.
2. Lists `release/` and `internal/release/` branches in that repo via `az repos ref list`.
3. Fetches the [dotnet/core releases index](https://github.com/dotnet/core/blob/main/release-notes/releases-index.json)
   to determine which .NET channels are supported (filtered by configured
   `supportPhases` and `minVersion`).
4. Filters branches to only those matching supported channels, returning them
   alongside `main`.

All results are cached per pipeline/repo for the lifetime of the process, with
per-key locking so concurrent callers share a single resolution.

## CLI commands

### `build-duty triage`

Run the full triage pipeline: collect sources and create work items, summarize
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
and an AI agent executes it. Agent output streams live to the terminal — you
can see reasoning, tool calls, and results in real time. Send follow-up
messages between turns for multi-turn conversations.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--include-acknowledged` | Include acknowledged items in the review list |

### `build-duty workitems list`

List tracked work items. Resolved items are hidden by default.

| Option | Description |
|---|---|
| `--status <status>` | Filter: `unresolved` or `resolved` |
| `--show-resolved` | Include resolved work items in output |
| `--limit <n>` | Max items to display |

### `build-duty workitems show`

Show full details for a single work item, including sources, summary, and history.

| Option | Description |
|---|---|
| `--id <id>` | Work item ID (required) |


## Architecture

```
BuildDuty.Cli          CLI entry-point, commands, rendering (Spectre.Console)
BuildDuty.Core         Domain model, work-item store, work item collectors, config
BuildDuty.AI           Copilot SDK adapter, skills, tools
BuildDuty.Tests        xUnit tests
```

### Data flow

```
build-duty triage
  ├─ Step 1: Work Item Collection  (deterministic, no AI)
  │   ├─ AzureDevOpsWorkItemCollector  → ADO pipeline runs + failure details
  │   ├─ GitHubIssueCollector          → GitHub issues
  │   └─ GitHubPrCollector             → GitHub PRs
  │   └─ WorkItemStore  ←── work items created/updated, state set (never status)
  │
  ├─ Step 2: AI Summarize  (summarize skill)
  │   ├─ CopilotAdapter       → Copilot SDK session
  │   ├─ SummarizeTools        → set_summary, get_task_log
  │   │   └─ AzureDevOpsBuildClient → deterministic timeline/log fetching
  │   └─ WorkItemStore         ←── summaries written
  │
  ├─ Step 3: AI Triage  (triage skill)
  │   ├─ CopilotAdapter       → Copilot SDK session + MCP servers
  │   │   ├─ gh CLI / MCP     → GitHub issue/PR details
  │   │   └─ az CLI / MCP     → ADO pipeline details
  │   ├─ WorkItemTriageTools   → resolve, status, links
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
