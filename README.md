# BuildDuty

A .NET CLI tool that streamlines build-duty workflows by collecting signals
from Azure DevOps and GitHub, analyzing them with AI, and reconciling the
results into trackable work items — all driven by repository-owned YAML
configuration.

## Why BuildDuty?

Build-duty engineers juggle multiple dashboards, pipelines, and issue trackers to
understand repository health. BuildDuty consolidates that work into a single,
auditable workflow:

- **Signal collection** — collects pipeline failures, GitHub issues, and PRs
  as signals. Each signal captures the raw context (URLs, failure details, log
  IDs) without interpretation.
- **AI-powered analysis** — each signal is independently analyzed by an AI
  agent that extracts cause, effect, and evidence using the GitHub Copilot SDK.
- **Work item reconciliation** — analyzed signals are grouped by root cause and
  reconciled into work items. The AI creates new work items, links related
  signals, and resolves items whose signals have cleared.
- **Interactive review** — select work items from a list and interact with an
  AI agent to investigate, resolve, or ask questions.
- **Release branch discovery** — automatically discovers active .NET release
  branches from the dotnet/core releases index.
- **Repo-owned configuration** — a `.build-duty.yml` file in your repository
  declares exactly what to monitor.

## Quick start

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/) (see `global.json`)
- [GitHub Copilot in the CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) —
  the Copilot SDK needs the `copilot` binary on your PATH
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli) (`az`) —
  required for Azure DevOps pipeline access
- [GitHub CLI](https://cli.github.com/) (`gh`) — required for GitHub issue/PR access

### 2. Authenticate

```bash
# Azure DevOps (needed for pipeline data)
az login

# GitHub (needed for issues, PRs, and Copilot SDK)
gh auth login
```

### 3. Build and install locally

```bash
git clone https://github.com/ellahathaway/build-duty.git
cd build-duty

# Build, test, pack, and install as a global tool
./eng/build.sh --install

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
            - name: ".*"
              labels:
                - "Build Break"
              state: open
```

### 5. Run the triage pipeline

```bash
# Full pipeline: collect signals → analyze → reconcile work items
build-duty triage run

# Resume a previous run by ID
build-duty triage run --resume <triage-run-id>

# List past triage runs
build-duty triage list

# Show details for a specific run
build-duty triage show --id <triage-run-id>

# Interactive review of work items
build-duty review

# List or inspect work items directly
build-duty workitem list
build-duty workitem show --id <work-item-id>
```

### Updating after code changes

After making changes to the source, rebuild and reinstall:

```bash
./eng/build.sh --install
```

## Building from source

### Linux / macOS

```bash
./eng/build.sh              # restore → build → test
./eng/build.sh --pack       # … + pack as NuGet
./eng/build.sh --install    # … + pack + install as global tool
```

### Windows

```powershell
.\eng\build.ps1              # restore → build → test
.\eng\build.ps1 -Pack        # … + pack as NuGet
.\eng\build.ps1 -Install     # … + pack + install as global tool
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
            - name: ".*"
              labels:
                - "Build Break"
              state: open
              authors:
                - app/dotnet-maestro
              excludeLabels:
                - wontfix
                - duplicate
          prs:
            - name: "dotnet-*"
              authors:
                - app/dotnet-maestro
                - dotnet-bot
              labels:
                - bug
              excludeLabels:
                - backport
                - "DO NOT MERGE"
```

### GitHub Issue and PR fields

Each entry under `issues` or `prs` is a pattern that matches issues or pull requests in a repository.
All fields except `name` are optional; omitting them means no filtering on that
dimension.

| Field | Type | Description |
|---|---|---|
| `name` | regex | Title regex — only whose title matches are included |
| `state` | `open` \| `closed` \| `all` | state filter (default: `open`) |
| `authors` | list of strings | Allowlist of login names. Use `app/<name>` for GitHub Apps (resolves to `<name>[bot]`). If omitted, all authors are included |
| `labels` | list of strings | Include only items that carry **all** of these labels (AND). If omitted, no label filtering is applied |
| `excludeLabels` | list of strings | Exclude any item that carries **any** of these labels (OR). If omitted, no items are excluded by label |
| `context` | string | Optional free-text context injected into the AI analysis prompt for signals from this pattern |

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

### Example configs

The [`configs/`](configs/) directory contains example `.build-duty.yml` files for
different build-duty roles on the .NET SDK team.

## CLI commands

### `build-duty triage run`

Run the full triage pipeline. The pipeline progresses through stages:
collect signals → analyze signals → update existing work items → create new work items.

Each stage is status-gated — a resumed run picks up where it left off.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--resume <id>` | Resume a specific triage run by ID |

### `build-duty triage list`

List past triage runs with their status and signal counts.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |

### `build-duty triage show`

Show details for a specific triage run.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--id <id>` | Triage run ID (required) |

### `build-duty review`

Interactively review work items. Unresolved items are displayed sorted by last
update. Select items from the list, then choose an action: mark as resolved,
ask an AI agent, or view details. The "ask agent" option opens a multi-turn
Copilot chat session with full tool access.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |

### `build-duty workitem list`

List tracked work items. Resolved items are hidden by default.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--show-resolved` | Include resolved work items in output |
| `--limit <n>` | Max items to display |

### `build-duty workitem show`

Show full details for a single work item, including linked signals, analyses,
and timestamps.

| Option | Description |
|---|---|
| `--config <path>` | Path to config file (default: auto-detect) |
| `--id <id>` | Work item ID (required) |
| `--verbose` | Also print linked signal details and analyses |

## Architecture

```
BuildDuty.Cli          CLI entry-point, commands, rendering (Spectre.Console)
BuildDuty.Core         Domain models, storage, signal collectors, config
BuildDuty.AI           Copilot SDK adapter, skills, tools
BuildDuty.Tests        xUnit tests
```

### Domain model

- **Signal** — a raw observation from a source (pipeline failure, GitHub issue,
  GitHub PR). Each signal has a type, URL, contextual info, and zero or more
  analyses.
- **SignalAnalysis** — an AI-generated breakdown of a single signal: what
  happened, why, and the supporting evidence.
- **WorkItem** — a trackable unit of work derived from one or more signals.
  Links to signals via `LinkedAnalysis` records. Can be resolved/reopened
  across triage runs.
- **TriageRun** — a record of a single triage execution, tracking which signals
  were collected and the current pipeline stage.

### Data flow

```
build-duty triage run
  │
  ├─ 1. Collect Signals  (deterministic, no AI)
  │   ├─ AzureDevOpsSignalCollector  → pipeline failures
  │   ├─ GitHubSignalCollector       → issues and PRs
  │   └─ StorageProvider  ←── signals saved as JSON
  │
  ├─ 2. Analyze Signals  (AI, per-signal)
  │   ├─ CopilotAdapter  → analyze-signal skill
  │   ├─ StorageTools     → read/write signal analyses
  │   └─ AzureDevOpsTools → fetch pipeline logs
  │
  ├─ 3. Update Work Items  (AI)
  │   ├─ CopilotAdapter  → update-workitems skill
  │   └─ StorageTools     → read/write work items and signals
  │
  └─ 4. Create Work Items  (AI)
      ├─ CopilotAdapter  → create-workitems skill
      └─ StorageTools     → create work items, link signals

build-duty review
  ├─ Load unresolved work items
  ├─ User selects items → action menu
  ├─ "Ask agent" → multi-turn Copilot chat (Review agent)
  └─ "Mark resolved" → update and save
```

### Local storage

Data is stored under `~/.build-duty/<name>/`, where `<name>` comes from the
`name` field in `.build-duty.yml`.

```
~/.build-duty/
└── sourcebuild-monitor/          ← matches name: sourcebuild-monitor
    ├── signals/                   # Signal JSON files
    ├── workitems/                 # Work item JSON files
    └── triage/                    # Triage run JSON files
```

## Dogfooding

Users are encouraged to use BuildDuty in their regular build-duty
rotations and report what they find. See [docs/dogfooding.md](docs/dogfooding.md)
for guidelines on getting started, filing feedback, and tracking adoption.

## Contributing

1. Fork and clone the repository.
2. Run `dotnet tool restore` to install local tooling.
3. Use `./eng/build.sh` (or `.\eng\build.ps1`) to build and test.
4. Open a pull request.

## License

See [LICENSE](LICENSE) for details.
