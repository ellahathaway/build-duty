using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class AiSettings : CommandSettings
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

internal sealed class AiCommand : AsyncCommand<AiSettings>
{
    private readonly Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> _adapterFactory;
    private readonly Func<string, string?, WorkItemStore> _wiStoreFactory;
    private readonly Func<string, string?, AiRunStore> _aiStoreFactory;

    public AiCommand(
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory,
        Func<string, string?, WorkItemStore> wiStoreFactory,
        Func<string, string?, AiRunStore> aiStoreFactory)
    {
        _adapterFactory = adapterFactory;
        _wiStoreFactory = wiStoreFactory;
        _aiStoreFactory = aiStoreFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AiSettings settings)
    {
        var configPath = settings.Config ?? Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found. Use --config to specify a path.");
        var config = BuildDutyConfig.LoadFromFile(configPath);

        var wiStore = _wiStoreFactory(config.Name, configPath);
        var aiStore = _aiStoreFactory(config.Name, configPath);
        var action = settings.Action!;

        if (settings.WorkItemId is not null)
        {
            var adapter = _adapterFactory(config, wiStore);
            var orchestrator = new AiOrchestrator(adapter, wiStore, aiStore);

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Running on {Markup.Escape(settings.WorkItemId)}...", async _ =>
                {
                    var result = await orchestrator.RunAsync(settings.WorkItemId, action);
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
                            // Each parallel run gets its own adapter/orchestrator
                            var adapter = _adapterFactory(config, wiStore);
                            var orchestrator = new AiOrchestrator(adapter, wiStore, aiStore);
                            try
                            {
                                var result = await orchestrator.RunAsync(id, action);
                                progressTask.Description = result.ExitCode == 0
                                    ? $"[green]✓[/] {Markup.Escape(id)}"
                                    : $"[red]✗[/] {Markup.Escape(id)}";
                                return result;
                            }
                            catch (Exception ex)
                            {
                                progressTask.Description = $"[red]✗[/] {Markup.Escape(id)}: {Markup.Escape(ex.Message)}";
                                return (AiRunResult?)null;
                            }
                            finally
                            {
                                progressTask.Increment(1);
                            }
                        });
                    }).ToList();

                    var results = await Task.WhenAll(tasks);

                    // Print results after progress completes
                    foreach (var result in results)
                    {
                        if (result is not null)
                            PrintResult(result);
                    }
                });
        }

        return 0;
    }

    private static void PrintResult(AiRunResult result)
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold blue]AI Result[/]");
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Work Item", Markup.Escape(result.WorkItemId));
        table.AddRow("Action", Markup.Escape(result.Job));
        table.AddRow("Exit Code", result.ExitCode == 0 ? "[green]0[/]" : $"[red]{result.ExitCode}[/]");
        table.AddRow("Summary", Markup.Escape(result.Summary ?? "(none)"));
        table.AddRow("Artifact", Markup.Escape(result.ArtifactPath ?? "(none)"));
        table.AddRow("Duration", $"{(result.FinishedUtc - result.StartedUtc).TotalSeconds:F1}s");
        AnsiConsole.Write(table);
    }
}
