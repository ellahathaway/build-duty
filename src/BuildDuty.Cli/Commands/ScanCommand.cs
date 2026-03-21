using System.ComponentModel;
using System.Text.Json;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class ScanSettings : CommandSettings
{
    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }
}

internal sealed class ScanCommand : AsyncCommand<ScanSettings>
{
    private readonly Func<string, string?, WorkItemStore> _storeFactory;
    private readonly Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> _adapterFactory;

    public ScanCommand(
        Func<string, string?, WorkItemStore> storeFactory,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory)
    {
        _storeFactory = storeFactory;
        _adapterFactory = adapterFactory;
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

        var store = _storeFactory(config.Name, configPath);

        // === Phase 1: Deterministic signal collection ===
        AnsiConsole.MarkupLine("\n[bold]Phase 1:[/] Collecting signals...");

        var collectionResults = new List<CollectionResult>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var tasks = new List<Task<CollectionResult>>();

                if (config.AzureDevOps is not null)
                {
                    var adoTask = ctx.AddTask("[bold]AzureDevOps[/]", maxValue: 1);
                    tasks.Add(Task.Run(async () =>
                    {
                        var collector = new AzureDevOpsSignalCollector(config.AzureDevOps);
                        var result = await collector.CollectAsync(store, default);
                        adoTask.Description = result.Success
                            ? $"[green]✓[/] AzureDevOps ({result.Signals.Count} signals)"
                            : $"[red]✗[/] AzureDevOps";
                        adoTask.Increment(1);
                        adoTask.StopTask();
                        return result;
                    }));
                }

                if (config.GitHub is not null)
                {
                    var ghCollector = new GitHubSignalCollector(config.GitHub);

                    if (config.GitHub.Repositories.Any(r => r.Issues is not null))
                    {
                        var issueTask = ctx.AddTask("[bold]GitHub Issues[/]", maxValue: 1);
                        tasks.Add(Task.Run(async () =>
                        {
                            var result = await ghCollector.CollectIssuesAsync(default);
                            issueTask.Description = result.Success
                                ? $"[green]✓[/] GitHub Issues ({result.Signals.Count} signals)"
                                : $"[red]✗[/] GitHub Issues";
                            issueTask.Increment(1);
                            issueTask.StopTask();
                            return result;
                        }));
                    }

                    if (config.GitHub.Repositories.Any(r => r.PullRequests is not null))
                    {
                        var prTask = ctx.AddTask("[bold]GitHub PRs[/]", maxValue: 1);
                        tasks.Add(Task.Run(async () =>
                        {
                            var result = await ghCollector.CollectPullRequestsAsync(default);
                            prTask.Description = result.Success
                                ? $"[green]✓[/] GitHub PRs ({result.Signals.Count} signals)"
                                : $"[red]✗[/] GitHub PRs";
                            prTask.Increment(1);
                            prTask.StopTask();
                            return result;
                        }));
                    }
                }

                collectionResults.AddRange(await Task.WhenAll(tasks));
            });

        // Show collection summary
        var allSignals = collectionResults.SelectMany(r => r.Signals).ToList();
        var failedCollections = collectionResults.Where(r => !r.Success).ToList();

        AnsiConsole.MarkupLine($"Collected [bold]{allSignals.Count}[/] signals.");

        foreach (var failure in failedCollections)
            AnsiConsole.MarkupLine($"  [red]✗[/] {failure.Source}: {Markup.Escape(failure.Error ?? "unknown error")}");

        if (allSignals.Count == 0 && failedCollections.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No failures found.[/]");
            return 0;
        }

        // === Phase 2: AI-powered triage ===
        AnsiConsole.MarkupLine("\n[bold]Phase 2:[/] AI triage...");

        var beforeItems = await store.ListAsync();
        var beforeIds = beforeItems.ToDictionary(i => i.Id, i => i.State);

        var signalsJson = JsonSerializer.Serialize(allSignals, new JsonSerializerOptions { WriteIndented = true });

        var prompt = $"""
            Use the scan-signals skill to triage the following collected signals.
            
            **Collected signals:**
            ```json
            {signalsJson}
            ```
            """;

        var scanResults = new List<ScanResult>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]AI triage[/]", maxValue: 1);

                await using var adapter = _adapterFactory(config, store);
                try
                {
                    var result = await adapter.ScanSourceAsync(
                        prompt, "triage", ScanTools.Skills,
                        CopilotSessionFactory.NoExtraServers(), ct: default);

                    progressTask.Description = result.Success
                        ? "[green]✓[/] AI triage"
                        : "[red]✗[/] AI triage";

                    scanResults.Add(result);
                }
                catch (Exception ex)
                {
                    progressTask.Description = "[red]✗[/] AI triage";
                    scanResults.Add(new ScanResult
                    {
                        Source = "triage",
                        Success = false,
                        Summary = $"Error: {ex.Message}",
                        Error = ex.ToString(),
                    });
                }
                finally
                {
                    progressTask.Increment(1);
                    progressTask.StopTask();
                }
            });

        // Report what changed
        var afterItems = await store.ListAsync();
        var afterIds = afterItems.ToDictionary(i => i.Id, i => i.State);
        var newCount = afterIds.Keys.Except(beforeIds.Keys).Count();
        var resolvedCount = afterIds.Count(a =>
            a.Value == WorkItemState.Resolved &&
            beforeIds.TryGetValue(a.Key, out var before) &&
            before != WorkItemState.Resolved);

        // Summary table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Phase");
        table.AddColumn("Source");
        table.AddColumn("Status");
        table.AddColumn("Duration");

        foreach (var r in collectionResults)
            table.AddRow(
                "Collection",
                Markup.Escape(r.Source),
                r.Success ? $"[green]✓[/] {r.Signals.Count} signals" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");

        foreach (var r in scanResults)
            table.AddRow(
                "Triage",
                Markup.Escape(r.Source),
                r.Success ? "[green]✓[/] complete" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");

        AnsiConsole.Write(table);

        foreach (var r in scanResults.Where(r => !r.Success))
            AnsiConsole.MarkupLine($"\n[red bold]Triage error:[/] {Markup.Escape(r.Summary)}");

        AnsiConsole.MarkupLine($"\n[bold]Scan complete.[/] New items: [green]{newCount}[/] | Resolved: [blue]{resolvedCount}[/] | Total tracked: {afterIds.Count}");
        return collectionResults.All(r => r.Success) && scanResults.All(r => r.Success) ? 0 : 1;
    }
}
