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
    public string? ConfigPath { get; set; }

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
        var configPath = settings.ConfigPath ?? Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found. Use --config to specify a path.");
        var config = BuildDutyConfig.LoadFromFile(configPath);
        AnsiConsole.MarkupLine($"Using config: [bold]{Markup.Escape(configPath)}[/] (name: {Markup.Escape(config.Name)})");

        var services = new List<ISignalService>();
        if (config.AzureDevOps is not null)
            services.Add(_azureDevOpsServiceFactory(config.AzureDevOps, settings.Ci));
        if (config.GitHub is not null)
            services.Add(_gitHubServiceFactory(config.GitHub));

        var store = _storeFactory(config.Name, configPath);
        var totalNew = 0;

        await AnsiConsole.Status()
            .StartAsync("Scanning...", async ctx =>
            {
                foreach (var svc in services)
                {
                    ctx.Status($"Collecting from {svc.SourceName}...");
                    var items = await svc.CollectAsync();
                    var newCount = 0;
                    foreach (var item in items)
                    {
                        if (!store.Exists(item.Id))
                        {
                            await store.SaveAsync(item);
                            newCount++;
                        }
                    }
                    totalNew += newCount;
                }
            });

        // Auto-resolve work items whose latest build now passes
        var totalResolved = 0;
        var adoService = services.OfType<AzureDevOpsSignalService>().FirstOrDefault();

        if (adoService is not null)
        {
            var allItems = await store.ListAsync();
            foreach (var item in allItems)
            {
                if (item.State == WorkItemState.Resolved || item.CorrelationId is null)
                    continue;

                // Check if latest build now passes
                if (adoService.PassingCorrelationIds.Contains(item.CorrelationId))
                {
                    if (item.State == WorkItemState.Unresolved)
                        item.TransitionTo(WorkItemState.InProgress,
                            "Build status changed", actor: "build-duty");

                    item.TransitionTo(WorkItemState.Resolved,
                        "Auto-resolved: latest build succeeded", actor: "build-duty");
                    await store.SaveAsync(item);
                    totalResolved++;
                }
            }
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Source");
        table.AddColumn("Status");
        foreach (var svc in services)
            table.AddRow(Markup.Escape(svc.SourceName), "[green]✓ collected[/]");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\nScan complete. [bold green]{totalNew}[/] new work item(s) created.");
        if (totalResolved > 0)
            AnsiConsole.MarkupLine($"[bold yellow]{totalResolved}[/] work item(s) auto-resolved.");
        return 0;
    }
}
