using System.ComponentModel;
using BuildDuty.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class ScanSettings : CommandSettings
{
    [CommandOption("--since")]
    [Description("Time window for signal collection (e.g. 24h)")]
    public string? Since { get; set; }

    [CommandOption("--profile")]
    [Description("Signal collection profile")]
    public string? Profile { get; set; }

    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }
}

internal sealed class ScanCommand : AsyncCommand<ScanSettings>
{
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

        var services = new ISignalService[]
        {
            new AzureDevOpsSignalService(),
            new GitHubSignalService()
        };

        var store = new WorkItemStore(Paths.WorkItemsDir(config.Name));
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

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Source");
        table.AddColumn("Status");
        foreach (var svc in services)
            table.AddRow(svc.SourceName, "[green]✓[/] collected");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\nScan complete. [bold green]{totalNew}[/] new work item(s) created.");
        return 0;
    }
}

