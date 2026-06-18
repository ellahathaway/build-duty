# BuildDuty

A .NET toolkit for build-duty workflows — collects signals from Azure DevOps
and GitHub, and provides Copilot skills and an MCP server for AI-powered triage.

## Architecture

BuildDuty is split into deterministic libraries (signal collection, config) and
AI integration (MCP server, Copilot skills):

```mermaid
flowchart TB
    subgraph copilot["Copilot Surface (CLI, VS Code, Workspace)"]
        skills["/triage, /analyze-*, /reconcile skills"]
        calls["MCP Tool Calls"]
        skills --> calls
    end

    calls --> buildDutyMcp["BuildDuty MCP<br/>(build-duty-mcp)<br/><br/>- collect signals<br/>- read config"]
    calls --> azdoMcp["AzDO MCP Server<br/><br/>- logs<br/>- builds"]
    calls --> githubMcp["GitHub MCP Server<br/><br/>- issues<br/>- PRs"]

    buildDutyMcp --> libraries["BuildDuty Libraries (no AI)<br/>BuildDuty.Configuration + BuildDuty.Signals"]
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

### Install the MCP server tool

The MCP server package (`ellahathaway.buildduty.mcp`) is hosted on GitHub Packages.
One-time setup:

```bash
# Ensure your GitHub CLI token has read:packages scope
gh auth refresh --scopes read:packages

# Add the GitHub Packages NuGet source (one-time)
dotnet nuget add source \
  --username YOUR_GITHUB_USERNAME \
  --password "$(gh auth token)" \
  --store-password-in-clear-text \
  --name github-ellahathaway \
  "https://nuget.pkg.github.com/ellahathaway/index.json"

# Install the MCP server tool globally
dotnet tool install --global ellahathaway.buildduty.mcp --version 0.0.1
```

> Marketplace plugins now run this setup automatically during `sessionStart`.
> Manual setup is still useful for standalone MCP usage.

## Usage

### Option 1: Copilot Marketplace (recommended)

Add the build-duty marketplace and install plugins:

```bash
# Add the marketplace
copilot plugin marketplace add ellahathaway/build-duty

# Install the triage plugin
copilot plugin install triage@build-duty
```

On first session start, the plugin auto-installs `BuildDuty.Mcp` if needed.
The setup hook checks:
- `gh` CLI version is 2.66.0+
- `gh auth` is logged in
- `az` is logged in
- GitHub Packages NuGet source is configured

Then you're ready to use the skills:

```
triage my pipelines
investigate the timeout in dotnet-source-build
```

Available plugins:

| Plugin | Description |
|--------|-------------|
| `triage` | Signal collection, pipeline/issue/PR analysis, and incident reconciliation |
| `reporting` | Triage summaries, incident timelines, and rotation handoff docs |
| `remediation` | Automated fixes for incidents (retry builds, etc.) |
| `config-management` | Managing `.build-duty.yml` configs |

Browse all available plugins with:

```bash
copilot plugin marketplace browse build-duty
```

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

### Option 3: MCP Server (standalone)

> **Prerequisite:** Install the MCP server tool first.
> See [Install the MCP server tool](#install-the-mcp-server-tool) above.

For standalone MCP server usage (e.g., custom clients or development), add the
following to your MCP client configuration:

```json
{
  "mcpServers": {
    "build-duty-mcp-server": {
      "command": "BuildDuty.Mcp"
    }
  }
}
```

If working in this repo, the MCP server is already configured in `.github/mcp.json`
and will be picked up automatically by GitHub Copilot.

Available tools:
- `build_duty_collect_signals` — collect signals from configured sources (requires `configPath`)
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

### Azure DevOps Pipeline fields

Each entry under `pipelines` defines a pipeline to monitor. All fields except `id` and `name` are optional.

| Field | Type | Description |
|---|---|---|
| `id` | int | Pipeline definition ID (required) |
| `name` | string | Display name for the pipeline (required) |
| `branches` | list | Branch names to monitor. When omitted, queries without a branch filter (latest build across all branches) |
| `release` | object | Auto-discover release branches (see below) |
| `age` | duration | Maximum build age to consider, e.g. `7d`, `24h`, `2d12h` |
| `status` | list | Build results to signal on (default: `Failed`, `PartiallySucceeded`, `Canceled`) |
| `timelineFilters` | list | Filter timeline records by type/name pattern |
| `context` | string | Free-text context injected into AI analysis prompts |

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

Each skill has a **single source of truth** under its owning plugin at
`.github/plugin/<plugin>/skills/<skill>/`. This is what the marketplace plugins
package and deliver when installed (see [Usage](#usage)) — the plugin `plugin.json`
files and `.github/plugin/marketplace.json` only ever reference these plugin-local
skill directories. Edit a skill **only** in its plugin location.

| Skill | Plugin | Description |
|-------|--------|-------------|
| `/triage` | `triage` | Full workflow — collect, analyze, reconcile |
| `/analyze-azure-devops-pipeline` | `triage` | Investigate a pipeline failure |
| `/analyze-github-issue` | `triage` | Investigate a GitHub issue |
| `/analyze-github-pull-request` | `triage` | Investigate a PR |
| `/reconcile-findings` | `triage` | Group and deduplicate findings |
| `/review-work-items` | `reporting` | Deep-dive into specific incidents |
| `/generate-handoff` | `reporting` | Produce a rotation handoff report |
| `/retry-build` | `remediation` | Retry a transient pipeline failure |
| `/validate-config` | `config-management` | Validate a `.build-duty.yml` config |

### Repo-local discovery (`.github/skills/`)

GitHub Copilot (CLI, VS Code, and other clients) also auto-discovers project
skills from `.github/skills/` when working **inside this repo**. To get that
zero-install discovery without duplicating any skill content, `.github/skills/<skill>`
is a **git symlink** that points at the skill's single source under
`.github/plugin/<plugin>/skills/<skill>/`. There is only ever one copy of each
skill to edit.

Symlinks materialize automatically on **Linux and macOS** (and on **Windows** with
[Developer Mode](https://learn.microsoft.com/windows/apps/get-started/enable-your-device-for-development)
enabled or an elevated checkout). On a Windows clone without symlink support, git
writes the links as text stubs; running `.\eng\build.ps1` repairs them via
[`eng/repair-skill-symlinks.ps1`](eng/repair-skill-symlinks.ps1) (enables
`core.symlinks` for the clone and re-checks-out `.github/skills`). If symlink
creation is still not permitted, the skills remain available by installing the
marketplace plugins.

### JSON output mode

The `/triage` skill supports a JSON output mode. Append "output as JSON" to the prompt to receive structured data instead of a markdown report:

```
/triage with config path: configs/.source-build-monitor.yml — output as JSON
```

Returns a `{ signals: [...], incidents: [...] }` object with typed signal/incident arrays. Useful for programmatic consumers like the triage dashboard canvas.

## Triage Dashboard Canvas

A canvas extension (`.github/extensions/triage-dashboard/`) that provides a live dashboard UI for triage results. Features:

- **Multi-config tabs** — monitor multiple `.build-duty.yml` configs simultaneously
- **Signal & incident display** — shows pipeline failures, GitHub issues, and grouped incidents
- **Investigate buttons** — spawns a new autonomous session to investigate any signal or incident
- **Run Triage** — kicks off a triage session that collects signals and pushes JSON results to the dashboard
- **Persistent state** — config selections and triage results survive across sessions

### Installation

The canvas is auto-discovered when cloning this repo. To install it standalone:

```bash
# From a GitHub repo folder URL
copilot extension install https://github.com/ellahathaway/build-duty/tree/main/.github/extensions/triage-dashboard
```

### Usage

Open the dashboard from any session:
```
open the triage-dashboard canvas with configPath "configs/.source-build-monitor.yml"
```

Or add configs interactively via the "+" button in the dashboard UI.

## Contributing

1. Fork and clone the repository.
2. Use `./eng/build.sh` (or `.\eng\build.ps1`) to build and test.
3. Open a pull request.
