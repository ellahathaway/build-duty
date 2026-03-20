using System.ComponentModel;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class ScanSettings : CommandSettings
{
    [CommandOption("--profile")]
    [Description("Signal collection profile")]
    public string? Profile { get; set; }

    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }

    [CommandOption("--ci")]
    [Description("Use CI credentials (env vars / Azure CLI) instead of interactive browser auth")]
    public bool Ci { get; set; }
}

internal sealed class ScanCommand : AsyncCommand<ScanSettings>
{
    private readonly Func<string, string?, WorkItemStore> _storeFactory;
    private readonly Func<AzureDevOpsConfig, bool, IAzureDevOpsSignalService> _azureDevOpsServiceFactory;
    private readonly Func<GitHubConfig, IGitHubSignalService> _gitHubServiceFactory;

    public ScanCommand(
        Func<string, string?, WorkItemStore> storeFactory,
        Func<AzureDevOpsConfig, bool, IAzureDevOpsSignalService> azureDevOpsServiceFactory,
        Func<GitHubConfig, IGitHubSignalService> gitHubServiceFactory)
    {
        _storeFactory = storeFactory;
        _azureDevOpsServiceFactory = azureDevOpsServiceFactory;
        _gitHubServiceFactory = gitHubServiceFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ScanSettings settings)
    {
        var configPath = settings.Config ?? Paths.ConfigPath();
        if (configPath is null)
        {
            AnsiConsole.MarkupLine("[red bold]Error:[/] No .build-duty.yml found in the repository root. Use --config to specify a path.");
            return 1;
        }

        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red bold]Error:[/] Config file not found: {configPath}");
            return 1;
        }

        var config = BuildDutyConfig.LoadFromFile(configPath);
        AnsiConsole.MarkupLine($"Using config: [bold]{configPath}[/] (name: [bold]{Markup.Escape(config.Name)}[/])");

        var services = new List<ISignalService>();
        if (config.AzureDevOps is not null)
            services.Add(_azureDevOpsServiceFactory(config.AzureDevOps, settings.Ci));
        if (config.GitHub is not null)
            services.Add(_gitHubServiceFactory(config.GitHub));

        var store = _storeFactory(config.Name, configPath);
        var totalNew = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning signals...", async ctx =>
            {
                foreach (var svc in services)
                {
                    ctx.Status($"Collecting from [bold]{svc.SourceName}[/]...");
                    var items = await svc.CollectAsync();
                    foreach (var item in items)
                    {
                        if (!store.Exists(item.Id))
                        {
                            await store.SaveAsync(item);
                            totalNew++;
                        }
                    }
                }
            });

        // Auto-resolve work items whose latest build now passes or whose branch is stale
        var totalResolved = 0;
        var adoService = services.OfType<AzureDevOpsSignalService>().FirstOrDefault();

        if (adoService is not null)
        {
            var allItems = await store.ListAsync();
            foreach (var item in allItems)
            {
                if (item.State == WorkItemState.Resolved || item.CorrelationId is null)
                    continue;

                bool shouldResolve = false;
                string? reason = null;

                // Check if latest build now passes
                if (adoService.PassingCorrelationIds.Contains(item.CorrelationId))
                {
                    shouldResolve = true;
                    reason = "Auto-resolved: latest build succeeded";
                }
                // Check if branch is stale (release pipeline only)
                else if (adoService.ReleasePipelinePrefixes.Count > 0)
                {
                    var matchesReleasePipeline = adoService.ReleasePipelinePrefixes
                        .Any(prefix => item.CorrelationId.StartsWith(prefix, StringComparison.Ordinal));

                    if (matchesReleasePipeline &&
                        !adoService.ActiveCorrelationIds.Contains(item.CorrelationId))
                    {
                        shouldResolve = true;
                        reason = "Auto-resolved: branch superseded by newer release";
                    }
                }

                if (shouldResolve)
                {
                    if (item.State == WorkItemState.Unresolved)
                        item.TransitionTo(WorkItemState.InProgress,
                            "Build status changed", actor: "build-duty");

                    item.TransitionTo(WorkItemState.Resolved, reason!, actor: "build-duty");
                    await store.SaveAsync(item);
                    totalResolved++;
                }
            }
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Source");
        table.AddColumn("Status");
        foreach (var svc in services)
            table.AddRow(svc.SourceName, "[green]✓[/] collected");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\nScan complete. [bold green]{totalNew}[/] new work item(s) created.");
        if (totalResolved > 0)
            AnsiConsole.MarkupLine($"[bold yellow]{totalResolved}[/] work item(s) auto-resolved.");
        return 0;
    }
}

