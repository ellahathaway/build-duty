using System.ComponentModel;
using System.Text.Json;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class TriageSettings : BaseSettings
{
    [CommandOption("--review")]
    [Description("Enter interactive review mode after triage")]
    public bool Review { get; set; }
}

internal sealed class TriageCommand : BaseCommand<TriageSettings>
{
    private readonly ISignalCollectorFactory _signalCollectorFactory;
    private readonly IStorageProvider _storageProvider;
    private readonly CopilotAdapter _copilotAdapter;

    private sealed record ReconcileMetrics
    {
        public int CreatedWorkItems { get; set; }
        public int UpdatedWorkItems { get; set; }
        public int ResolvedWorkItems { get; set; }
        public int ReopenedWorkItems { get; set; }
    }

    public TriageCommand(
        IBuildDutyConfigProvider configProvider,
        ISignalCollectorFactory signalCollectorFactory,
        IStorageProvider storageProvider,
        CopilotAdapter copilotAdapter)
        : base(configProvider)
    {
        _signalCollectorFactory = signalCollectorFactory;
        _storageProvider = storageProvider;
        _copilotAdapter = copilotAdapter;
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, TriageSettings settings)
    {
        // Start a new triage run
        var triageRun = new TriageRun();
        await _storageProvider.SaveTriageRunAsync(triageRun);

        // === Collect signals ===
        AnsiConsole.MarkupLine("\n[bold][/]Collecting signals...");

        triageRun.Status = TriageRunStatus.CollectingSignals;
        await _storageProvider.SaveTriageRunAsync(triageRun);
        var collectedSignalIds = new List<string>();

        await RunWithProgressAsync(async ctx =>
            {
                var azureDevOpsSignalsTask = CollectSignalsAsync<AzureDevOpsConfig>(ctx);
                var githubSignalsTask = CollectSignalsAsync<GitHubConfig>(ctx);

                var results = await Task.WhenAll(azureDevOpsSignalsTask, githubSignalsTask);
                collectedSignalIds.AddRange(results.SelectMany(ids => ids));
            });

        triageRun.SignalIds = collectedSignalIds;
        if (collectedSignalIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No signals collected. Nothing to triage.[/]");
            triageRun.Status = TriageRunStatus.Done;
            await _storageProvider.SaveTriageRunAsync(triageRun);
            return 0;
        }
        AnsiConsole.MarkupLine($"[green]✓[/] Collected [bold]{collectedSignalIds.Count}[/] signals.");

        triageRun.Status = TriageRunStatus.SummarizingSignals;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        // === AI-powered summarization ===
        AnsiConsole.MarkupLine("\n[bold][/] Summarizing signals...");

        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Summarization[/]", maxValue: collectedSignalIds.Count);

                var maxParallelSummaries = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
                using var semaphore = new SemaphoreSlim(maxParallelSummaries);

                string summarizePrompt = """
                    Summarize the following signal.
                    """;

                var summarizeTasks = collectedSignalIds.Select(signalId => Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await _copilotAdapter.RunSignalActionAsync(signalId, summarizePrompt);
                    }
                    finally
                    {
                        semaphore.Release();
                        progressTask.Increment(1);
                    }
                }));

                await Task.WhenAll(summarizeTasks);

                progressTask.StopTask();
            });

        AnsiConsole.MarkupLine($"[green]✓[/] Summarized [bold]{collectedSignalIds.Count}[/] signals.");

        // === Reconcile work items (create/update/resolve) ===
        triageRun.Status = TriageRunStatus.ReconcilingWorkItems;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        AnsiConsole.MarkupLine("\n[bold][/] Reconciling work items...");

        var metrics = await ReconcileWorkItemsWithAiAsync(collectedSignalIds, triageRun.Id);

        AnsiConsole.MarkupLine(
            $"[green]✓[/] Reconciled work items. Created [bold]{metrics.CreatedWorkItems}[/], updated [bold]{metrics.UpdatedWorkItems}[/], resolved [bold]{metrics.ResolvedWorkItems}[/], reopened [bold]{metrics.ReopenedWorkItems}[/].");

        triageRun.Status = TriageRunStatus.Done;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        return 0;
    }

    private async Task<ReconcileMetrics> ReconcileWorkItemsWithAiAsync(
        IEnumerable<string> signalIds,
        string triageId)
    {
        var distinctSignalIds = signalIds.Distinct(StringComparer.Ordinal).ToList();
        if (distinctSignalIds.Count == 0)
        {
            return new ReconcileMetrics();
        }

        string reconcileWorkItemsPrompt = $$"""
            Run `reconcile-work-items` for triage run `{{triageId}}` on the provided signal IDs.
            Return only the metrics JSON required by that skill.
            """;

        var response = await _copilotAdapter.RunSignalSetActionAsync(distinctSignalIds, reconcileWorkItemsPrompt);
        var metrics = ParseReconcileMetrics(response);

        return metrics;
    }

    private static ReconcileMetrics ParseReconcileMetrics(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidOperationException("AI reconciliation returned an empty response; expected JSON metrics.");
        }

        string candidate = response.Trim();
        int firstBrace = candidate.IndexOf('{');
        int lastBrace = candidate.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            candidate = candidate.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        var metrics = JsonSerializer.Deserialize<ReconcileMetrics>(candidate, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (metrics is null)
        {
            throw new InvalidOperationException("AI reconciliation response could not be parsed into metrics JSON.");
        }

        return metrics;
    }

    private async Task<List<string>> CollectSignalsAsync<TConfig>(dynamic ctx) where TConfig : class
    {
        string source = typeof(TConfig).Name;
        var configTask = ctx.AddTask($"[bold]{source}[/]", maxValue: 1);
        try
        {
            var signalCollector = _signalCollectorFactory.CreateCollector<TConfig>();
            if (signalCollector is null)
            {
                configTask.Description = $"[yellow]↷[/] {source} (not configured)";
                return [];
            }

            var signalIds = await signalCollector.CollectAsync();
            configTask.Description = $"[green]✓[/] {source} ({signalIds.Count} signals)";
            return signalIds;
        }
        catch (Exception ex)
        {
            configTask.Description = $"[red]✗[/] {source}";
            throw new Exception($"Error collecting signals from {source}: {ex.Message}", ex);
        }
        finally
        {
            configTask.Increment(1);
            configTask.StopTask();
        }
    }

    private static async Task RunWithProgressAsync(Func<ProgressContext, Task> action)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(action);
    }
}
