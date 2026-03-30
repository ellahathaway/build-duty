using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
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
    private readonly IBuildDutyConfigProvider _configProvider;
    private readonly WorkItemStore _store;
    private readonly Func<WorkItemStore, CopilotAdapter> _adapterFactory;

    public TriageCommand(
        IBuildDutyConfigProvider configProvider,
        WorkItemStore store,
        Func<WorkItemStore, CopilotAdapter> adapterFactory) : base(configProvider)
    {
        _configProvider = configProvider;
        _store = store;
        _adapterFactory = adapterFactory;
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, TriageSettings settings)
    {
        var branchResolver = new ReleaseBranchResolver();

        // === Step 1: Gather work items ===
        AnsiConsole.MarkupLine("\n[bold]Step 1:[/] Gathering work items...");

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

                if (_configProvider.Get().AzureDevOps is not null)
                {
                    var adoTask = ctx.AddTask("[bold]AzureDevOps[/]", maxValue: 1);
                    tasks.Add(Task.Run(async () =>
                    {
                        var azureDevOpsConfig = _configProvider.Get().AzureDevOps ?? throw new InvalidOperationException("Azure DevOps configuration is missing.");
                        var collector = new AzureDevOpsWorkItemCollector(azureDevOpsConfig, branchResolver);
                        var result = await collector.CollectAsync(_store, default);
                        adoTask.Description = result.Success
                            ? $"[green]✓[/] AzureDevOps ({result.Sources.Count} sources)"
                            : $"[red]✗[/] AzureDevOps";
                        adoTask.Increment(1);
                        adoTask.StopTask();
                        return result;
                    }));
                }

                if (_configProvider.Get().GitHub is not null)
                {
                    var gitHubConfig = _configProvider.Get().GitHub ?? throw new InvalidOperationException("GitHub configuration is missing.");
                    var ghCollector = new GitHubWorkItemCollector(gitHubConfig);
                    var allRepos = gitHubConfig.Organizations.SelectMany(o => o.Repositories);

                    if (allRepos.Any(r => r.Issues is not null))
                    {
                        var issueTask = ctx.AddTask("[bold]GitHub Issues[/]", maxValue: 1);
                        tasks.Add(Task.Run(async () =>
                        {
                            var result = await ghCollector.CollectIssuesAsync(_store, default);
                            issueTask.Description = result.Success
                                ? $"[green]✓[/] GitHub Issues ({result.Sources.Count} sources)"
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
                            var result = await ghCollector.CollectPullRequestsAsync(_store, default);
                            prTask.Description = result.Success
                                ? $"[green]✓[/] GitHub PRs ({result.Sources.Count} sources)"
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
        var allSources = collectionResults.SelectMany(r => r.Sources).ToList();
        var failedCollections = collectionResults.Where(r => !r.Success).ToList();
        var totalCreated = collectionResults.Sum(r => r.Created);
        var totalUpdated = collectionResults.Sum(r => r.Updated);
        var totalClosed = collectionResults.Sum(r => r.Closed);

        var parts = new List<string> { $"created [green]{totalCreated}[/]" };
        if (totalUpdated > 0)
        {
            parts.Add($"updated [yellow]{totalUpdated}[/]");
        }

        if (totalClosed > 0)
        {
            parts.Add($"closed [dim]{totalClosed}[/]");
        }

        AnsiConsole.MarkupLine($"Gathered [bold]{allSources.Count}[/] sources — {string.Join(", ", parts)}.");

        foreach (var failure in failedCollections)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] {failure.Source}: {Markup.Escape(failure.Error ?? "unknown error")}");
        }

        if (allSources.Count == 0 && failedCollections.Count == 0 && totalCreated == 0)
        {
            AnsiConsole.MarkupLine("[green]No failures found.[/]");
            return 0;
        }

        // === Step 2: AI-powered summarization ===
        AnsiConsole.MarkupLine("\n[bold]Step 2:[/] Summarizing new work items...");

        var beforeItems = await _store.ListAsync();
        var beforeIds = beforeItems.ToDictionary(i => i.Id, i => i.IsResolved);

        // Only summarize work items that need it: no summary yet, or source
        // has been updated since the last summary (e.g. issue/PR changed).
        var toSummarize = (await _store.ListAsync(resolved: false))
            .Where(i => i.NeedsSummary)
            .ToList();

        // Add AzDO MCP server if configured — GitHub MCP is built-in
        var adoOrgUrl = _configProvider.Get().AzureDevOps?.Organizations.FirstOrDefault()?.Url;
        var mcpServers = adoOrgUrl is not null
            ? CopilotSessionFactory.AdoPipelineServers(ExtractOrgName(adoOrgUrl))
            : CopilotSessionFactory.NoExtraServers();

        var summarizeResults = new List<ScanResult>();

        if (toSummarize.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No new work items to summarize.[/]");
        }
        else
        {
            var itemsList = string.Join("\n\n", toSummarize.Select(i =>
            {
                var sourceRef = i.Sources.FirstOrDefault();
                var refUrl = sourceRef?.Ref ?? "(none)";
                var sourceType = sourceRef?.Type ?? "(none)";
                var failureDetails = sourceRef?.Metadata?.GetValueOrDefault("failureDetails") ?? "";
                var details = string.IsNullOrWhiteSpace(failureDetails)
                    ? ""
                    : $"\n  Failure details:\n  {failureDetails.Replace("\n", "\n  ")}";
                return $"- {i.Id} | type={sourceType} | ref={refUrl} | title={i.Title}{details}";
            }));

            var summarizePrompt = $"""
                Use the summarize skill to write summaries for the following work
                items that need summarizing.

                For pipeline failures (ado-pipeline-run), the failure details
                (failed tasks, error messages, log IDs) are included below.
                **ALWAYS call `get_task_log` for each failed task's logId** to
                get the full error context — inline error messages are often
                truncated. The build URL is in the ref field.

                For GitHub items, use `gh` CLI or MCP servers to read details.

                Then call `set_work_item_summary` for each item with a 1-3 sentence
                summary focusing on what failed/changed and why.

                **Work items to summarize ({toSummarize.Count}):**
                {itemsList}
                """;

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new SpinnerColumn(),
                    new ElapsedTimeColumn())
                .StartAsync(async ctx =>
                {
                    var progressTask = ctx.AddTask("[bold]AI summarization[/]", maxValue: 1);

                    await using var adapter = _adapterFactory(_store);
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

        // Report Step 2 errors
        foreach (var r in summarizeResults.Where(r => !r.Success))
        {
            AnsiConsole.MarkupLine($"\n[red bold]Summarize error:[/] {Markup.Escape(r.Summary)}");
        }

        // === Step 3: AI-powered triage ===
        AnsiConsole.MarkupLine("\n[bold]Step 3:[/] Triaging work items...");

        // Capture pre-triage statuses to detect what the AI changed
        var preTriage = (await _store.ListAsync(resolved: false))
            .ToDictionary(i => i.Id, i => (Status: i.Status, LinkedItems: i.LinkedItems.ToList()));

        // Only triage items that are new or were just (re-)summarized
        var allUnresolved = (await _store.ListAsync(resolved: false)).ToList();
        var toTriage = allUnresolved.Where(i => i.NeedsTriage).ToList();
        var contextOnly = allUnresolved.Where(i => !i.NeedsTriage).ToList();

        var triageResults = new List<ScanResult>();

        if (toTriage.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No work items need triage.[/]");
        }
        else
        {
            string FormatItem(WorkItem i)
            {
                var sourceRef = i.Sources.FirstOrDefault();
                var refUrl = sourceRef?.Ref ?? "(none)";
                var sourceType = sourceRef?.Type ?? "(none)";
                var summary = string.IsNullOrWhiteSpace(i.Summary) ? "(none)" : i.Summary;
                var links = i.LinkedItems.Count > 0 ? string.Join(", ", i.LinkedItems) : "(none)";
                var failureDetails = sourceRef?.Metadata?.GetValueOrDefault("failureDetails");
                var detailsLine = string.IsNullOrWhiteSpace(failureDetails)
                    ? ""
                    : $"\n  failureDetails: {failureDetails.Replace("\n", "\n  ")}";
                var linkedPrs = sourceRef?.Metadata?.GetValueOrDefault("linkedPrs");
                var linkedPrsLine = string.IsNullOrWhiteSpace(linkedPrs)
                    ? ""
                    : $"\n  linkedPrs: {linkedPrs}";
                return $"- {i.Id} | status={i.Status} | state={i.State ?? "(none)"} | type={sourceType} | ref={refUrl} | links={links} | summary={summary} | title={i.Title}{detailsLine}{linkedPrsLine}";
            }

            var triageList = string.Join("\n", toTriage.Select(FormatItem));

            var contextSection = contextOnly.Count > 0
                ? $"""

                **Existing unresolved items (context for cross-referencing — do NOT update these, but DO link new items to them and set status to `tracked` when appropriate):**
                {string.Join("\n", contextOnly.Select(FormatItem))}
                """
                : "";

            var triagePrompt = $"""
                Use the triage skill to process the following work items.

                Work items have already been created by the collection step and
                summarized by the summarize step. Your job is to:

                1. **Cross-reference first** — Before setting any status, check if
                   the item is related to any other item in the full list (both
                   triage items AND existing context items below). If a pipeline
                   failure matches a GitHub issue by error signature, component,
                   or topic, link them with `link_work_items`.
                   **IMPORTANT:** Only link/correlate items if their failure signatures
                   (error messages, failed tasks, test names) match specifically. Two
                   failures in the same category (e.g. both "Component Governance") but
                   with different specific alerts are NOT the same issue.
                2. **Then set status** — If the item was just linked to an issue or PR,
                   set it to `tracked`. Otherwise determine the appropriate status
                   per source type (use `update_work_item_status`).
                3. Resolve work items that are no longer relevant based on context
                   (use `resolve_work_item`).

                Each work item has a `state` field set by collection:
                - `new` — first time collected, needs initial triage
                - `updated` — source has changed since last collection
                - `closed` — source is no longer active (build passing, PR merged, issue closed)

                For `closed` items: resolve them with `resolve_work_item`.
                For `updated` items: re-evaluate the item. If new linked PRs
                appeared (see `linkedPrs` field), set status to `tracked`. Otherwise
                re-assess based on the change.
                For `new` items: first check if the item matches any existing
                issue or PR (see context items below), or if it has `linkedPrs`.
                If yes, link them and set status to `tracked`. If no match, set
                to `needs-investigation`.

                Status semantics:
                - `tracked` — has a linked issue or PR addressing it
                - `monitoring` — engineer is watching this item, wants to stay informed
                - `acknowledged` — engineer has dismissed this, no action needed

                After processing, items are marked as `stable` automatically.

                Each item includes its current summary, linked items, and failure
                details — use these to determine statuses and cross-references.
                **Do NOT query external services** — everything you need is here.

                **Work items needing triage ({toTriage.Count}):**
                {triageList}
                {contextSection}
                """;

            await AnsiConsole.Progress()
                .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new SpinnerColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("[bold]AI triage[/]", maxValue: 1);

                await using var adapter = _adapterFactory(_store);
                try
                {
                    var result = await adapter.ScanSourceAsync(
                        triagePrompt, "triage", WorkItemTriageTools.Skills,
                        mcpServers, ct: default);

                    progressTask.Description = result.Success
                        ? "[green]✓[/] AI triage"
                        : "[red]✗[/] AI triage";

                    triageResults.Add(result);
                }
                catch (Exception ex)
                {
                    progressTask.Description = "[red]✗[/] AI triage";
                    triageResults.Add(new ScanResult
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

            // Mark triaged items as stable
            foreach (var item in toTriage)
            {
                var refreshed = await _store.LoadAsync(item.Id);
                if (refreshed is not null && refreshed.State is not null && refreshed.State != "stable")
                {
                    refreshed.State = "stable";
                    await _store.SaveAsync(refreshed);
                }
            }
        }

        // === Confirm new correlations ===
        var postTriageItems = await _store.ListAsync(resolved: false);
        var correlationsToConfirm = new List<(WorkItem Item, string LinkedId, WorkItem Linked)>();

        foreach (var item in postTriageItems)
        {
            if (!preTriage.TryGetValue(item.Id, out var pre))
            {
                continue;
            }

            var newLinks = item.LinkedItems.Except(pre.LinkedItems).ToList();
            foreach (var lid in newLinks)
            {
                var linked = await _store.LoadAsync(lid);
                if (linked is not null)
                {
                    correlationsToConfirm.Add((item, lid, linked));
                }
            }
        }

        if (correlationsToConfirm.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold yellow]Confirm Correlations[/] — {correlationsToConfirm.Count} new link(s)") { Justification = Justify.Left });
            AnsiConsole.WriteLine();

            foreach (var (item, linkedId, linked) in correlationsToConfirm)
            {
                // Skip if the link was already removed by a previous rejection in this loop
                if (!item.LinkedItems.Contains(linkedId))
                {
                    continue;
                }

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn(new GridColumn().Width(3));
                grid.AddColumn();
                grid.AddRow(
                    new Panel(Markup.Escape(item.Summary ?? item.Title))
                    {
                        Header = new PanelHeader(Markup.Escape(item.Id)),
                        Border = BoxBorder.Rounded,
                        Padding = new Padding(1, 0),
                    },
                    new Markup("[bold yellow] ↔ [/]"),
                    new Panel(Markup.Escape(linked.Summary ?? linked.Title))
                    {
                        Header = new PanelHeader(Markup.Escape(linkedId)),
                        Border = BoxBorder.Rounded,
                        Padding = new Padding(1, 0),
                    });
                AnsiConsole.Write(grid);

                var confirmed = AnsiConsole.Confirm("Accept this correlation?", defaultValue: true);
                if (!confirmed)
                {
                    var reason = AnsiConsole.Ask<string>("Why is this incorrect?");

                    // Remove the bidirectional link
                    item.LinkedItems.Remove(linkedId);
                    linked.LinkedItems.Remove(item.Id);

                    // If the item was set to "tracked" solely because of this link, revert
                    if (item.Status == "tracked" &&
                        (!preTriage.TryGetValue(item.Id, out var pre2) || pre2.Status != "tracked"))
                    {
                        item.SetStatus("needs-review", $"Correlation rejected: {reason}");
                    }

                    item.TriagedAtUtc = DateTime.UtcNow;
                    await _store.SaveAsync(item);
                    await _store.SaveAsync(linked);
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓ Confirmed.[/]\n");
                }
            }
        }

        // Report what changed
        var afterItems = await _store.ListAsync();
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
        {
            table.AddRow(
                "Collection",
                Markup.Escape(r.Source),
                r.Success ? $"[green]✓[/] {r.Sources.Count} sources, {r.Created} new, {r.Updated} updated" + (r.Closed > 0 ? $", {r.Closed} closed" : "") : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");
        }

        foreach (var r in summarizeResults)
        {
            table.AddRow(
                "Summarize",
                Markup.Escape(r.Source),
                r.Success ? "[green]✓[/] complete" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");
        }

        foreach (var r in triageResults)
        {
            table.AddRow(
                "Triage",
                Markup.Escape(r.Source),
                r.Success ? "[green]✓[/] complete" : "[red]✗[/] failed",
                $"{r.Duration.TotalSeconds:F1}s");
        }

        AnsiConsole.Write(table);

        foreach (var r in summarizeResults.Concat(triageResults).Where(r => !r.Success))
        {
            AnsiConsole.MarkupLine($"\n[red bold]Error ({Markup.Escape(r.Source)}):[/] {Markup.Escape(r.Summary)}");
        }

        AnsiConsole.MarkupLine($"\n[bold]Triage complete.[/] New items: [green]{newCount}[/] | Resolved: [blue]{resolvedCount}[/] | Total tracked: {afterIds.Count}");

        var triageSuccess = collectionResults.All(r => r.Success) && summarizeResults.All(r => r.Success) && triageResults.All(r => r.Success);

        // Enter interactive review mode if --review was passed
        if (settings.Review)
        {
            var unresolvedCount = afterIds.Count(a => !a.Value);
            if (unresolvedCount > 0)
            {
                AnsiConsole.MarkupLine($"\n[dim]Entering review mode ({unresolvedCount} unresolved items)...[/]");
                await ReviewCommand.RunReviewAsync(_configProvider, _store, _adapterFactory);
            }
        }

        return triageSuccess ? 0 : 1;
    }

    /// <summary>Extract org name from an AzDO URL like "https://dev.azure.com/dnceng".</summary>
    private static string ExtractOrgName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.Trim('/').Split('/')[0];
        }

        return url;
    }
}
