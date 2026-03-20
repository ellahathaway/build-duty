using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class AiRunSettings : CommandSettings
{
    [CommandOption("--workitem")]
    [Description("Work item ID to analyze")]
    public string? WorkItemId { get; set; }

    [CommandOption("--job")]
    [Description("Job type (summarize, cluster, root-cause, next-actions)")]
    [DefaultValue("summarize")]
    public string Job { get; set; } = "summarize";

    [CommandOption("--state")]
    [Description("Batch: run against all items in this state")]
    public string? State { get; set; }

    [CommandOption("--limit")]
    [Description("Batch: max items to process")]
    public int? Limit { get; set; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkItemId) && string.IsNullOrWhiteSpace(State))
            return ValidationResult.Error("Specify --workitem <id> or --state <state> for batch mode.");
        return ValidationResult.Success();
    }
}

internal sealed class AiRunCommand : AsyncCommand<AiRunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, AiRunSettings settings)
    {
        var routerPath = Paths.RouterYamlPath();
        var router = RouterManifest.LoadFromFile(routerPath);
        var adapter = new CopilotAdapter();
        var wiStore = new WorkItemStore(Paths.WorkItemsDir());
        var aiStore = new AiRunStore(Paths.AiRunsDir());
        var orchestrator = new AiOrchestrator(router, adapter, wiStore, aiStore);

        if (settings.WorkItemId is not null)
        {
            await RunSingleAsync(orchestrator, settings.WorkItemId, settings.Job);
        }
        else if (settings.State is not null)
        {
            var filter = settings.State.ToLowerInvariant() switch
            {
                "unresolved" => WorkItemState.Unresolved,
                "inprogress" => WorkItemState.InProgress,
                "resolved" => WorkItemState.Resolved,
                _ => throw new ArgumentException($"Unknown state '{settings.State}'")
            };

            var items = await wiStore.ListAsync(filter, settings.Limit);
            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No work items in state '{Markup.Escape(settings.State)}'.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"Running [bold]'{Markup.Escape(settings.Job)}'[/] on [bold]{items.Count}[/] item(s)...\n");
            foreach (var item in items)
            {
                await RunSingleAsync(orchestrator, item.Id, settings.Job);
            }
        }

        return 0;
    }

    private static async Task RunSingleAsync(AiOrchestrator orchestrator, string workItemId, string job)
    {
        AiRunResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Running '{job}' on {workItemId}...", async _ =>
            {
                result = await orchestrator.RunAsync(workItemId, job);
            });

        if (result is null) return;

        var table = new Table().Border(TableBorder.Rounded).Title($"[bold blue]AI Run: {Markup.Escape(job)}[/]");
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddRow("Work Item", Markup.Escape(result.WorkItemId));
        table.AddRow("Skill", Markup.Escape(result.Skill));
        table.AddRow("Exit Code", result.ExitCode == 0 ? "[green]0[/]" : $"[red]{result.ExitCode}[/]");
        table.AddRow("Summary", Markup.Escape(result.Summary ?? "(none)"));
        table.AddRow("Artifact", Markup.Escape(result.ArtifactPath ?? "(none)"));
        table.AddRow("Duration", $"{(result.FinishedUtc - result.StartedUtc).TotalSeconds:F1}s");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }
}
