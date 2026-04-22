using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using GitHub.Copilot.SDK;
using Spectre.Console;
using Spectre.Console.Cli;

using CommandContext = Spectre.Console.Cli.CommandContext;

namespace BuildDuty.Cli.Commands;

internal sealed class TriageRunSettings : BaseSettings
{
    [CommandOption("--resume")]
    [Description("The ID of the triage run to resume.")]
    public string Resume { get; set; } = string.Empty;
}

internal sealed class TriageRunCommand : BaseCommand<TriageRunSettings>
{
    private readonly ISignalCollectorFactory _signalCollectorFactory;
    private readonly IStorageProvider _storageProvider;
    private readonly CopilotAdapter _copilotAdapter;

    public TriageRunCommand(
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

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, TriageRunSettings settings)
    {
        TriageRun triageRun;
        if (!string.IsNullOrEmpty(settings.Resume))
        {
            // We are resuming a previous triage run
            triageRun = await _storageProvider.GetTriageRunAsync(settings.Resume);
            if (triageRun is null)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Could not find triage run with ID {settings.Resume}");
                return 1;
            }
            if (triageRun.Status == TriageRunStatus.Done)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Triage run {settings.Resume} is already Done and cannot be resumed.");
                return 1;
            }
        }
        else
        {
            triageRun = new TriageRun();
            await _storageProvider.SaveTriageRunAsync(triageRun);
        }

        await CollectAllSignalsAsync(triageRun);
        if (triageRun.SignalIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]↷[/] No signals collected. Nothing to triage.");
            triageRun.Status = TriageRunStatus.Done;
            await _storageProvider.SaveTriageRunAsync(triageRun);
            return 0;
        }

        await AnalyzeCollectedSignalsAsync(triageRun);
        await UpdateWorkItemsAsync(triageRun);
        await CreateWorkItemsAsync(triageRun);

        triageRun.Status = TriageRunStatus.Done;
        await _storageProvider.SaveTriageRunAsync(triageRun);
        return 0;
    }

    private async Task CollectAllSignalsAsync(TriageRun triageRun)
    {
        if (triageRun.Status != TriageRunStatus.CollectingSignals && triageRun.Status != TriageRunStatus.NotStarted)
        {
            return;
        }

        foreach (var signalId in triageRun.SignalIds)
        {
            await _storageProvider.DeleteSignalAsync(signalId);
        }
        triageRun.SignalIds.Clear();

        AnsiConsole.MarkupLine("\n[bold]Collecting signals...[/]");

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
        if (collectedSignalIds.Count > 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Collected [bold]{collectedSignalIds.Count}[/] signals.");
        }
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

    private async Task AnalyzeCollectedSignalsAsync(TriageRun triageRun)
    {
        if (triageRun.Status != TriageRunStatus.CollectingSignals && triageRun.Status != TriageRunStatus.AnalyzingSignals)
        {
            return;
        }

        triageRun.Status = TriageRunStatus.AnalyzingSignals;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        AnsiConsole.MarkupLine("\n[bold]Analyzing signals...[/]");

        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Analysis[/]", maxValue: triageRun.SignalIds.Count);

                var maxParallelSummaries = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
                using var semaphore = new SemaphoreSlim(maxParallelSummaries);

                var summarizeTasks = triageRun.SignalIds.Select(signalId => Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        await AnalyzeSingleSignalAsync(triageRun.Id, signalId);
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

        // Count results from storage — analyses tagged with this triage run's ID
        var signals = await Task.WhenAll(triageRun.SignalIds.Select(id => _storageProvider.GetSignalAsync(id)));
        var triageAnalyses = signals
            .SelectMany(s => s.Analyses)
            .Where(a => a.LastTriageId == triageRun.Id);

        int created = triageAnalyses.Count(a => a.Status == AnalysisStatus.New);
        int updated = triageAnalyses.Count(a => a.Status == AnalysisStatus.Updated);
        int resolved = triageAnalyses.Count(a => a.Status == AnalysisStatus.Resolved);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Updated {updated} analyses");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Created {created} analyses");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Resolved {resolved} analyses");
    }

    /// <summary>
    /// Analyze a single signal. Signals with a resolved collection reason are handled
    /// deterministically — all unresolved analyses are resolved with a canned reason and
    /// the AI agent is not invoked.
    /// </summary>
    private async Task AnalyzeSingleSignalAsync(
        string triageRunId, string signalId)
    {
        var signal = await _storageProvider.GetSignalAsync(signalId);

        if (signal.IsResolvedCollectionReason)
        {
            await ResolveSignalAnalysesAsync(triageRunId, signal);
            return;
        }

        string analyzePrompt = $"Triage run: `{triageRunId}`. Analyze the following signal: `{signalId}`.";
        await _copilotAdapter.RunSessionAsync(analyzePrompt, throwAfterRetries: true);

        // Reload the signal from storage to get the latest analyses
        signal = await _storageProvider.GetSignalAsync(signalId);
        if (signal.Analyses.Count == 0)
        {
            Failures.Add(signalId, $"Signal {signalId} has no analyses after agent completed.");
            return;
        }

        if (!signal.Analyses.Any(a => a.LastTriageId == triageRunId))
        {
            Failures.Add(signalId, $"Signal {signalId} has no analyses for triage run {triageRunId} after agent completed.");
            return;
        }

        foreach (var analysis in signal.Analyses.Where(a => a.LastTriageId == triageRunId && a.Status == AnalysisStatus.Resolved && string.IsNullOrWhiteSpace(a.ResolutionReason)))
        {
            Failures.Add(analysis.Id, $"Analysis {analysis.Id} on signal {signalId} is resolved but has no resolution reason.");
        }
    }

    /// <summary>
    /// Deterministically resolve all unresolved analyses for a signal that has a resolved
    /// collection reason (Resolved, NotFound, OutOfScope).
    /// </summary>
    private async Task ResolveSignalAnalysesAsync(string triageRunId, Signal signal)
    {
        string resolutionReason = signal.CollectionReason switch
        {
            SignalCollectionReason.Resolved => "Signal resolved — the monitored condition is no longer active.",
            SignalCollectionReason.NotFound => "Signal not found — the resource may have been deleted or aged out.",
            SignalCollectionReason.OutOfScope => "Signal is out of scope — monitoring configuration no longer includes this resource.",
            _ => throw new InvalidOperationException($"Unexpected resolved collection reason: {signal.CollectionReason}")
        };

        var unresolvedAnalyses = signal.Analyses
            .Where(a => a.Status != AnalysisStatus.Resolved)
            .ToList();

        foreach (var analysis in unresolvedAnalyses)
        {
            var index = signal.Analyses.IndexOf(analysis);
            signal.Analyses[index] = analysis with
            {
                Status = AnalysisStatus.Resolved,
                ResolutionReason = resolutionReason,
                LastTriageId = triageRunId
            };
        }

        await _storageProvider.SaveSignalAsync(signal);
    }

    private async Task UpdateWorkItemsAsync(TriageRun triageRun)
    {
        if (triageRun.Status != TriageRunStatus.AnalyzingSignals && triageRun.Status != TriageRunStatus.UpdatingWorkItems)
        {
            return;
        }

        triageRun.Status = TriageRunStatus.UpdatingWorkItems;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        AnsiConsole.MarkupLine("\n[bold]Updating work items...[/]");

        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Updating work items[/]", autoStart: true, maxValue: 1);

                string prompt = $"/update-workitems Triage ID: {triageRun.Id}.";
                await _copilotAdapter.RunSessionAsync(prompt, throwAfterRetries: true);

                progressTask.Increment(1);
                progressTask.StopTask();
            });

        var workItems = await _storageProvider.GetWorkItemsAsync();
        int updatedCount = workItems.Count(wi => wi.LastTriageId == triageRun.Id);
        int resolvedCount = await ResolveWorkItemsAsync(triageRun.Id, workItems);

        AnsiConsole.MarkupLine($"[green]\u2713[/] Updated {updatedCount} existing work items");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Resolved {resolvedCount} existing work items");
    }

    /// <summary>
    /// Resolve work items whose linked analyses are all resolved (or have no linked analyses).
    /// Returns the number of work items resolved.
    /// </summary>
    private async Task<int> ResolveWorkItemsAsync(string triageRunId, ICollection<WorkItem> workItems)
    {
        var signalCache = new Dictionary<string, Signal>();
        int resolvedCount = 0;

        foreach (var workItem in workItems.Where(wi => !wi.Resolved))
        {
            if (await AllLinkedAnalysesResolvedAsync(workItem, signalCache))
            {
                workItem.Resolved = true;
                workItem.LastTriageId = triageRunId;
                await _storageProvider.SaveWorkItemAsync(workItem);
                resolvedCount++;
            }
        }

        return resolvedCount;
    }

    private async Task<bool> AllLinkedAnalysesResolvedAsync(WorkItem workItem, Dictionary<string, Signal> signalCache)
    {
        if (workItem.LinkedAnalyses.Count == 0 ||
            workItem.LinkedAnalyses.All(la => la.AnalysisIds.Count == 0))
        {
            return true;
        }

        foreach (var link in workItem.LinkedAnalyses)
        {
            if (!signalCache.TryGetValue(link.SignalId, out var signal))
            {
                signal = await _storageProvider.GetSignalAsync(link.SignalId);
                signalCache[link.SignalId] = signal;
            }

            foreach (var analysisId in link.AnalysisIds)
            {
                var analysis = signal.Analyses.FirstOrDefault(a => a.Id == analysisId);
                if (analysis is null || analysis.Status != AnalysisStatus.Resolved)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task CreateWorkItemsAsync(TriageRun triageRun)
    {
        if (triageRun.Status != TriageRunStatus.UpdatingWorkItems && triageRun.Status != TriageRunStatus.CreatingWorkItems)
        {
            return;
        }

        triageRun.Status = TriageRunStatus.CreatingWorkItems;
        await _storageProvider.SaveTriageRunAsync(triageRun);

        AnsiConsole.MarkupLine("\n[bold]Creating work items for orphaned analyses...[/]");

        // Snapshot existing work item IDs before AI runs
        var existingIds = new HashSet<string>(
            (await _storageProvider.GetWorkItemsAsync()).Select(wi => wi.Id));

        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Creating work items[/]", autoStart: true, maxValue: 1);

                string prompt = $"/create-workitems Triage ID: {triageRun.Id}.";
                await _copilotAdapter.RunSessionAsync(prompt, throwAfterRetries: true);

                await ValidateCreatedWorkItemsAsync();

                progressTask.Increment(1);
                progressTask.StopTask();
            });

        // Count results from storage — new work items that didn't exist before
        var allWorkItems = await _storageProvider.GetWorkItemsAsync();
        int workItemsCreated = allWorkItems.Count(wi => !existingIds.Contains(wi.Id));

        AnsiConsole.MarkupLine($"[green]\u2713[/] Created {workItemsCreated} new work items.");

        async Task ValidateCreatedWorkItemsAsync()
        {
            var signals = await Task.WhenAll(
                triageRun.SignalIds.Select(id => _storageProvider.GetSignalAsync(id)));
            var workItems = (await _storageProvider.GetWorkItemsAsync()).ToList();

            var linkedAnalysisIds = workItems
                .SelectMany(wi => wi.LinkedAnalyses.SelectMany(la => la.AnalysisIds))
                .ToHashSet();
            var signalIdsInWorkItems = workItems
                .SelectMany(wi => wi.LinkedAnalyses.Select(la => la.SignalId))
                .ToHashSet();

            foreach (var signal in signals)
            {
                foreach (var analysis in signal.Analyses)
                {
                    if (analysis.Status != AnalysisStatus.Resolved
                        && analysis.LastTriageId == triageRun.Id
                        && !linkedAnalysisIds.Contains(analysis.Id))
                    {
                        Failures.Add(analysis.Id,
                            $"Analysis {analysis.Id} on signal {signal.Id} is not linked to any work item.");
                    }
                }

                if (signal.Analyses.Any(a => a.Status != AnalysisStatus.Resolved)
                    && !signalIdsInWorkItems.Contains(signal.Id))
                {
                    Failures.Add(signal.Id,
                        $"Signal {signal.Id} has active analyses but is not represented in any work item.");
                }
            }
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

internal sealed class TriageListCommand : BaseCommand<BaseSettings>
{
    private readonly IStorageProvider _storageProvider;

    public TriageListCommand(
        IBuildDutyConfigProvider configProvider,
        IStorageProvider storageProvider)
        : base(configProvider)
    {
        _storageProvider = storageProvider;
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, BaseSettings settings)
    {
        var triageRuns = await _storageProvider.GetTriageRunsAsync();
        if (!triageRuns.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No triage runs found.[/]");
            return 0;
        }

        foreach (var run in triageRuns)
        {
            AnsiConsole.MarkupLine($"[green]{run.Id}[/] - {run.Status}");
        }
        return 0;
    }
}

internal sealed class TriageShowSettings : BaseSettings
{
    [CommandOption("--id")]
    [Description("The ID of the triage run to show.")]
    public string Id { get; set; } = string.Empty;

    public override ValidationResult Validate()
    {
        return string.IsNullOrWhiteSpace(Id)
            ? ValidationResult.Error("--id is required")
            : ValidationResult.Success();
    }
}

internal sealed class TriageShowCommand : BaseCommand<TriageShowSettings>
{
    IStorageProvider _storageProvider;

    public TriageShowCommand(
        IBuildDutyConfigProvider configProvider,
        IStorageProvider storageProvider)
        : base(configProvider)
    {
        _storageProvider = storageProvider;
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, TriageShowSettings settings)
    {
        var triageRun = await _storageProvider.GetTriageRunAsync(settings.Id);
        // Show the details of the triage run, including its ID, status, and any associated work items and signals
        AnsiConsole.MarkupLine($"[green]{triageRun.Id}[/] - {triageRun.Status}");
        var workItems = await _storageProvider.GetWorkItemsForTriageRunAsync(triageRun.Id);

        AnsiConsole.MarkupLine("[yellow]Work Items:[/]");
        foreach (var workItem in workItems)
        {
            AnsiConsole.MarkupLine($"  [green]{workItem.Id}[/] - {workItem.IssueSignature}");
        }

        var signals = await Task.WhenAll(triageRun.SignalIds.Select(si => _storageProvider.GetSignalAsync(si)));
        AnsiConsole.MarkupLine("[yellow]Signals:[/]");
        foreach (var signal in signals)
        {
            AnsiConsole.MarkupLine($"  [green]{signal.Id}[/] - {signal.Type}");
        }

        return 0;
    }
}
