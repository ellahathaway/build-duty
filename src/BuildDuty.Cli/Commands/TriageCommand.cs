using System.ComponentModel;
using System.Text.Json;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class TriageSettings : CommandSettings
{
    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }
}

internal sealed class TriageCommand : AsyncCommand<TriageSettings>
{
    private readonly Func<string, string?, WorkItemStore> _storeFactory;
    private readonly Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> _adapterFactory;

    public TriageCommand(
        Func<string, string?, WorkItemStore> storeFactory,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory)
    {
        _storeFactory = storeFactory;
        _adapterFactory = adapterFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TriageSettings settings)
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

        // === Step 1: Collect signals ===
        AnsiConsole.MarkupLine("\n[bold]Step 1:[/] Collecting signals...");

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
                    var allRepos = config.GitHub.Organizations.SelectMany(o => o.Repositories);

                    if (allRepos.Any(r => r.Issues is not null))
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

                    if (allRepos.Any(r => r.PullRequests is { Count: > 0 }))
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

        // === Step 2: AI-powered triage ===
        AnsiConsole.MarkupLine("\n[bold]Step 2:[/] Triaging signals...");

        var beforeItems = await store.ListAsync();
        var beforeIds = beforeItems.ToDictionary(i => i.Id, i => i.IsResolved);

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

        // Report Step 2 errors
        foreach (var r in scanResults.Where(r => !r.Success))
            AnsiConsole.MarkupLine($"\n[red bold]Triage error:[/] {Markup.Escape(r.Summary)}");

        // === Step 3: AI-powered summarization ===
        AnsiConsole.MarkupLine("\n[bold]Step 3:[/] Summarizing work items...");

        // Summarize ALL unresolved items — source state may have changed since
        // the last run (PRs merged, builds re-run, issues updated).
        var toSummarize = (await store.ListAsync(resolved: false)).ToList();

        var summarizeResults = new List<ScanResult>();

        if (toSummarize.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No unresolved work items to summarize.[/]");
        }
        else
        {
            var itemsList = string.Join("\n", toSummarize.Select(i =>
            {
                var signalRef = i.Signals.FirstOrDefault()?.Ref ?? "(none)";
                var signalType = i.Signals.FirstOrDefault()?.Type ?? "(none)";
                var existing = string.IsNullOrWhiteSpace(i.Summary) ? "none" : "exists";
                return $"- {i.Id} | status={i.Status} | type={signalType} | ref={signalRef} | summary={existing} | title={i.Title}";
            }));

            var summarizePrompt = $"""
                Use the summarize skill to write or refresh summaries for the following
                unresolved work items.

                For each item, fetch current details from the source (build logs via
                az CLI for pipelines, gh CLI for GitHub items) and call
                set_work_item_summary with an up-to-date summary.

                Items marked summary=exists may have stale summaries — always fetch
                fresh data and update if the source state has changed.

                Keep summaries to 1-3 sentences focusing on what failed/changed and why.

                **Unresolved work items ({toSummarize.Count}):**
                {itemsList}
                """;

            var adoOrgUrl = config.AzureDevOps?.Organizations.FirstOrDefault()?.Url;
            var mcpServers = adoOrgUrl is not null
                ? CopilotSessionFactory.AdoPipelineServers(ExtractOrgName(adoOrgUrl))
                : CopilotSessionFactory.NoExtraServers();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new SpinnerColumn(),
                    new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var progressTask = ctx.AddTask("[bold]AI summarization[/]", maxValue: 1);

                    await using var adapter = _adapterFactory(config, store);
                    try
                    {
                        var result = await adapter.ScanSourceAsync(
                            summarizePrompt, "summarize", SummarizeTools.Skills,
                            mcpServers, ct: default);

                        progressTask.Description = result.Success
                            ? "[green]✓[/] AI summarization"
                            : "[red]✗[/] AI summarization";

                        summarizeResults.Add(result);
                    }
                    catch (Exception ex)
                    {
                        progressTask.Description = "[red]✗[/] AI summarization";
                        summarizeResults.Add(new ScanResult
                        {
                            Source = "summarize",
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
        }

        // === Step 4: AI-powered correlation ===
        AnsiConsole.MarkupLine("\n[bold]Step 4:[/] Correlating work items...");

        // Re-fetch — summaries were written in step 3
        var unresolvedItems = (await store.ListAsync(resolved: false)).ToList();

        var correlationResults = new List<ScanResult>();

        if (unresolvedItems.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No unresolved work items to correlate.[/]");
        }
        else
        {
            var itemsList = string.Join("\n", unresolvedItems.Select(i =>
            {
                var signalRef = i.Signals.FirstOrDefault()?.Ref ?? "(none)";
                var signalType = i.Signals.FirstOrDefault()?.Type ?? "(none)";
                var summary = string.IsNullOrWhiteSpace(i.Summary) ? "(none)" : i.Summary;
                return $"- {i.Id} | status={i.Status} | type={signalType} | ref={signalRef} | summary={summary} | title={i.Title}";
            }));

            var correlationPrompt = $"""
                Use the correlate-signals skill to update statuses and cross-reference
                the following unresolved work items.

                Each item includes its current summary — use it to understand what the
                item is about when determining statuses and cross-references.

                For all items, determine and update the type-specific status.
                Cross-reference related items where applicable.

                **Unresolved work items ({unresolvedItems.Count}):**
                {itemsList}
                """;

            // Add AzDO MCP server if configured — GitHub MCP is built-in
            var adoOrgUrl = config.AzureDevOps?.Organizations.FirstOrDefault()?.Url;
            var mcpServers = adoOrgUrl is not null
                ? CopilotSessionFactory.AdoPipelineServers(ExtractOrgName(adoOrgUrl))
                : CopilotSessionFactory.NoExtraServers();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new SpinnerColumn(),
                    new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var progressTask = ctx.AddTask("[bold]AI correlation[/]", maxValue: 1);

                    await using var adapter = _adapterFactory(config, store);
                    try
                    {
                        var result = await adapter.ScanSourceAsync(
                            correlationPrompt, "correlation", CorrelationTools.Skills,
                            mcpServers, ct: default);

                        progressTask.Description = result.Success
                            ? "[green]✓[/] AI correlation"
                            : "[red]✗[/] AI correlation";

                        correlationResults.Add(result);
                    }
                    catch (Exception ex)
                    {
                        progressTask.Description = "[red]✗[/] AI correlation";
                        correlationResults.Add(new ScanResult
                        {
                            Source = "correlation",
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
        }

        // Report what changed
        var afterItems = await store.ListAsync();
        var afterIds = afterItems.ToDictionary(i => i.Id, i => i.IsResolved);
        var newCount = afterIds.Keys.Except(beforeIds.Keys).Count();
        var resolvedCount = afterIds.Count(a =>
            a.Value &&
            beforeIds.TryGetValue(a.Key, out var wasBefore) &&
            !wasBefore);

        // Summary table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Step");
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

        foreach (var r in summarizeResults)
            table.AddRow(
                "Summarize",
                Markup.Escape(r.Source),
                r.Success ? "[green]✓[/] complete" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");

        foreach (var r in correlationResults)
            table.AddRow(
                "Correlation",
                Markup.Escape(r.Source),
                r.Success ? "[green]✓[/] complete" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");

        AnsiConsole.Write(table);

        foreach (var r in scanResults.Concat(summarizeResults).Concat(correlationResults).Where(r => !r.Success))
            AnsiConsole.MarkupLine($"\n[red bold]Error ({Markup.Escape(r.Source)}):[/] {Markup.Escape(r.Summary)}");

        AnsiConsole.MarkupLine($"\n[bold]Scan complete.[/] New items: [green]{newCount}[/] | Resolved: [blue]{resolvedCount}[/] | Total tracked: {afterIds.Count}");
        return collectionResults.All(r => r.Success) && scanResults.All(r => r.Success) && correlationResults.All(r => r.Success) && summarizeResults.All(r => r.Success) ? 0 : 1;
    }

    /// <summary>Extract org name from an AzDO URL like "https://dev.azure.com/dnceng".</summary>
    private static string ExtractOrgName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.Trim('/').Split('/')[0];
        return url;
    }
}
