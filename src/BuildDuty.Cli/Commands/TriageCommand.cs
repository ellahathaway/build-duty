using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

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

    private record AnalysisResult(int AnalysesUpdated, int AnalysesCreated, int AnalysesResolved);
    private sealed record UpdateWorkItemsResult(int WorkItemsUpdated, int WorkItemsResolved);
    private sealed record CreateWorkItemsResult(int WorkItemsCreated);

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

        var analysisResults = new List<AnalysisResult>();
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
                        string analyzePrompt = $"Triage run: `{triageRun.Id}`. Analyze the following signal: `{signalId}`.";
                        await using var session = await _copilotAdapter.CreateSessionAsync(
                            streaming: false,
                            agent: CopilotAdapter.Agents.Analyze,
                            throwAfterRetries: true);

                        try
                        {
                            var result = await _copilotAdapter.RunPromptAsync(session, analyzePrompt);
                            return JsonSerializer.Deserialize<AnalysisResult?>(ExtractJson(result?.Data?.Content ?? ""), new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                            }) ?? throw new InvalidOperationException($"AI did not return a valid response for analysis of signal {signalId}.");
                        }
                        finally
                        {
                            await _copilotAdapter.DeleteSessionAsync(session);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        progressTask.Increment(1);
                    }
                }));

                analysisResults = (await Task.WhenAll(summarizeTasks)).ToList();

                progressTask.StopTask();
            });

        AnsiConsole.MarkupLine($"[green]\u2713[/] Updated {analysisResults.Sum(r => r?.AnalysesUpdated ?? 0)} analyses");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Created {analysisResults.Sum(r => r?.AnalysesCreated ?? 0)} analyses");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Resolved {analysisResults.Sum(r => r?.AnalysesResolved ?? 0)} analyses");
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

        UpdateWorkItemsResult? updatedWorkItems = null;
        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Updating work items[/]", autoStart: true, maxValue: 1);

                string prompt = $"/update-workitems Triage ID: {triageRun.Id}.";
                await using var session = await _copilotAdapter.CreateSessionAsync(
                    agent: CopilotAdapter.Agents.Reconcile,
                    throwAfterRetries: true);

                try
                {
                    var result = await _copilotAdapter.RunPromptAsync(session, prompt);
                    updatedWorkItems = JsonSerializer.Deserialize<UpdateWorkItemsResult?>(ExtractJson(result?.Data?.Content ?? ""), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                }
                finally
                {
                    await _copilotAdapter.DeleteSessionAsync(session);
                }

                progressTask.Increment(1);
                progressTask.StopTask();
            });

        if (updatedWorkItems is null)
        {
            throw new InvalidOperationException("AI did not return a valid response for work item updates.");
        }

        AnsiConsole.MarkupLine($"[green]\u2713[/] Updated {updatedWorkItems.WorkItemsUpdated} existing work items");
        AnsiConsole.MarkupLine($"[green]\u2713[/] Resolved {updatedWorkItems.WorkItemsResolved} existing work items");
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

        CreateWorkItemsResult? createdWorkItems = null;
        await RunWithProgressAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]Creating work items[/]", autoStart: true, maxValue: 1);

                string prompt = $"/create-workitems Triage ID: {triageRun.Id}.";
                await using var session = await _copilotAdapter.CreateSessionAsync(
                    agent: CopilotAdapter.Agents.Reconcile,
                    throwAfterRetries: true);

                try
                {
                    var result = await _copilotAdapter.RunPromptAsync(session, prompt);
                    createdWorkItems = JsonSerializer.Deserialize<CreateWorkItemsResult?>(ExtractJson(result?.Data?.Content ?? ""), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                }
                finally
                {
                    await _copilotAdapter.DeleteSessionAsync(session);
                }

                progressTask.Increment(1);
                progressTask.StopTask();
            });

        if (createdWorkItems is null)
        {
            throw new InvalidOperationException("AI did not return a valid response for work item creation.");
        }

        AnsiConsole.MarkupLine($"[green]\u2713[/] Created {createdWorkItems.WorkItemsCreated} new work items.");
    }

    private static readonly Regex s_jsonObject = new(@"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the first JSON object from an AI response that may contain
    /// markdown fences, prose, or other non-JSON wrapping.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var match = s_jsonObject.Match(text);
        return match.Success ? match.Value : text.Trim();
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
