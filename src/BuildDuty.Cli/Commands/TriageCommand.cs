using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class TriageSettings : CommandSettings
{
    [CommandOption("--id")]
    [Description("Work item ID to analyze")]
    public string? WorkItemId { get; set; }

    [CommandOption("--action")]
    [Description("Free-form description of what to do (e.g. 'summarize this failure', 'what is the root cause?')")]
    public string? Action { get; set; }

    [CommandOption("--state")]
    [Description("Filter work items by state for batch/selection")]
    public string? State { get; set; }

    [CommandOption("--show-resolved")]
    [Description("Include resolved work items in selection")]
    public bool ShowResolved { get; set; }

    [CommandOption("--limit")]
    [Description("Max items to process")]
    public int? Limit { get; set; }

    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Action))
            return ValidationResult.Error("Specify --action to describe what you want the AI to do.");
        return ValidationResult.Success();
    }
}

internal sealed class TriageCommand : AsyncCommand<TriageSettings>
{
    private readonly Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> _adapterFactory;
    private readonly Func<string, string?, WorkItemStore> _wiStoreFactory;
    private readonly Func<string, string?, TriageStore> _triageStoreFactory;

    public TriageCommand(
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory,
        Func<string, string?, WorkItemStore> wiStoreFactory,
        Func<string, string?, TriageStore> triageStoreFactory)
    {
        _adapterFactory = adapterFactory;
        _wiStoreFactory = wiStoreFactory;
        _triageStoreFactory = triageStoreFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TriageSettings settings)
    {
        var configPath = settings.Config ?? Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found. Use --config to specify a path.");
        var config = BuildDutyConfig.LoadFromFile(configPath);

        var wiStore = _wiStoreFactory(config.Name, configPath);
        var triageStore = _triageStoreFactory(config.Name, configPath);
        var action = settings.Action!;
        var adoOrgName = config.AzureDevOps?.Organizations.FirstOrDefault()?.Url.TrimEnd('/').Split('/').LastOrDefault();

        if (settings.WorkItemId is not null)
        {
            var adapter = _adapterFactory(config, wiStore);

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Running on {Markup.Escape(settings.WorkItemId)}...", async _ =>
                {
                    var result = await RunTriageAsync(adapter, wiStore, triageStore, settings.WorkItemId, action, adoOrgName);
                    PrintResult(result);
                });
        }
        else
        {
            WorkItemState? stateFilter = settings.State?.ToLowerInvariant() switch
            {
                "unresolved" => WorkItemState.Unresolved,
                "inprogress" => WorkItemState.InProgress,
                "resolved" => WorkItemState.Resolved,
                null => null,
                _ => throw new ArgumentException($"Unknown state '{settings.State}'")
            };

            var items = await wiStore.ListAsync(stateFilter, settings.Limit);

            // Exclude resolved by default unless --show-resolved or --state resolved
            if (!settings.ShowResolved && stateFilter != WorkItemState.Resolved)
                items = items.Where(i => i.State != WorkItemState.Resolved).ToList();

            if (items.Count == 0)
            {
                var scope = stateFilter.HasValue ? $" in state '{Markup.Escape(settings.State!)}'" : "";
                AnsiConsole.MarkupLine($"[yellow]No work items found{scope}.[/]");
                return 0;
            }

            var selected = AnsiConsole.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title("Select work items to run against:")
                    .PageSize(15)
                    .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                    .AddChoices(items.Select(i =>
                    {
                        var state = i.State.ToString().ToLowerInvariant();
                        var stateColor = state switch
                        {
                            "unresolved" => "red",
                            "inprogress" => "yellow",
                            "resolved" => "green",
                            _ => "grey"
                        };
                        return $"{i.Id}  [{stateColor}]{state}[/]  {Markup.Escape(i.Title)}";
                    })));

            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No work items selected.[/]");
                return 0;
            }

            var ids = selected.Select(s => s.Split("  ", 2)[0]).ToList();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new SpinnerColumn(),
                    new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var tasks = ids.Select(id =>
                    {
                        var progressTask = ctx.AddTask(Markup.Escape(id), maxValue: 1);
                        return Task.Run(async () =>
                        {
                            var adapter = _adapterFactory(config, wiStore);
                            try
                            {
                                var result = await RunTriageAsync(adapter, wiStore, triageStore, id, action, adoOrgName);
                                progressTask.Description = result.Success
                                    ? $"[green]✓[/] {Markup.Escape(id)}"
                                    : $"[red]✗[/] {Markup.Escape(id)}";
                                return result;
                            }
                            catch (Exception ex)
                            {
                                progressTask.Description = $"[red]✗[/] {Markup.Escape(id)}: {Markup.Escape(ex.Message)}";
                                return (TriageResult?)null;
                            }
                            finally
                            {
                                progressTask.Increment(1);
                            }
                        });
                    }).ToList();

                    var results = await Task.WhenAll(tasks);

                    foreach (var result in results)
                    {
                        if (result is not null)
                            PrintResult(result);
                    }
                });
        }

        return 0;
    }

    /// <summary>
    /// Run triage for a single work item: transition state, look up prior run,
    /// invoke the AI, and persist the result.
    /// </summary>
    private static async Task<TriageResult> RunTriageAsync(
        CopilotAdapter adapter,
        WorkItemStore wiStore,
        TriageStore triageStore,
        string workItemId,
        string action,
        string? adoOrgName = null,
        CancellationToken ct = default)
    {
        var workItem = await wiStore.LoadAsync(workItemId, ct)
            ?? throw new InvalidOperationException($"Work item '{workItemId}' not found.");

        // Look up prior run so the AI can build on previous analysis
        TriageResult? priorRun = null;
        if (workItem.State == WorkItemState.InProgress)
            priorRun = await triageStore.FindLatestForWorkItemAsync(workItemId, ct);

        // Transition to InProgress
        if (workItem.State == WorkItemState.Unresolved)
        {
            workItem.TransitionTo(WorkItemState.InProgress, $"Triage: {action}");
            await wiStore.SaveAsync(workItem, ct);
        }

        var runId = IdGenerator.NewTriageRunId();
        var mcpServers = CopilotSessionFactory.AllServers(adoOrgName);
        var result = await adapter.TriageAsync(workItem, action, runId, TriageTools.Skills, mcpServers, priorRun, ct);

        await triageStore.SaveAsync(result, ct);
        return result;
    }

    private static void PrintResult(TriageResult result)
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold blue]Triage Result[/]");
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Work Item", Markup.Escape(result.WorkItemId));
        table.AddRow("Action", Markup.Escape(result.Action));
        table.AddRow("Status", result.Success ? "[green]success[/]" : "[red]failed[/]");
        table.AddRow("Summary", Markup.Escape(result.Summary ?? "(none)"));
        table.AddRow("Duration", $"{result.Duration.TotalSeconds:F1}s");
        AnsiConsole.Write(table);
    }
}
