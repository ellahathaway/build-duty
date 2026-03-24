using System.ComponentModel;
using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class ReviewSettings : CommandSettings
{
    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }

    [CommandOption("--include-acknowledged")]
    [Description("Include acknowledged items in the review list")]
    public bool IncludeAcknowledged { get; set; }
}

internal sealed class ReviewCommand : AsyncCommand<ReviewSettings>
{
    private readonly Func<string, string?, WorkItemStore> _storeFactory;
    private readonly Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> _adapterFactory;

    public ReviewCommand(
        Func<string, string?, WorkItemStore> storeFactory,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory)
    {
        _storeFactory = storeFactory;
        _adapterFactory = adapterFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ReviewSettings settings)
    {
        var configPath = settings.Config ?? Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found.");
        var config = BuildDutyConfig.LoadFromFile(configPath);
        var store = _storeFactory(config.Name, configPath);

        return await RunReviewAsync(config, store, _adapterFactory,
            includeAcknowledged: settings.IncludeAcknowledged);
    }

    public static async Task<int> RunReviewAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>? adapterFactory = null,
        bool includeAcknowledged = false)
    {
        var agents = new List<BackgroundAgent>();

        try
        {
            while (true)
            {
                AnsiConsole.Clear();

                var items = (await store.ListAsync(resolved: false))
                    .Where(i => includeAcknowledged || i.Status != "acknowledged")
                    .ToList();

                // Exclude items already claimed by an active agent
                var claimedIds = agents
                    .Where(a => a.State != AgentState.Dismissed)
                    .SelectMany(a => a.Items.Select(i => i.Id))
                    .ToHashSet();
                var availableItems = items.Where(i => !claimedIds.Contains(i.Id)).ToList();

                if (availableItems.Count == 0 && agents.Count(a => a.State != AgentState.Dismissed) == 0)
                {
                    AnsiConsole.MarkupLine("[dim]No unresolved work items to review.[/]");
                    return 0;
                }

                // Group by type then status
                var groups = availableItems
                    .GroupBy(i => i.Signals.FirstOrDefault()?.Type ?? "(unknown)")
                    .OrderBy(g => g.Key)
                    .SelectMany(typeGroup => typeGroup
                        .GroupBy(i => i.Status)
                        .OrderBy(sg => sg.Key)
                        .Select(sg => new ItemGroup(typeGroup.Key, sg.Key, sg.ToList())))
                    .Where(g => g.Items.Count > 0)
                    .ToList();

                // Build menu choices
                var choices = new List<string>();

                // Agent status option (if any non-dismissed agents exist)
                var activeAgents = agents.Where(a => a.State != AgentState.Dismissed).ToList();
                string? agentLabel = null;
                if (activeAgents.Count > 0)
                {
                    var running = activeAgents.Count(a => a.State == AgentState.Running);
                    var done = activeAgents.Count(a => a.State == AgentState.Done);
                    var errored = activeAgents.Count(a => a.State == AgentState.Error);

                    var parts = new List<string>();
                    if (running > 0) parts.Add($"[yellow]{running} running[/]");
                    if (done > 0) parts.Add($"[green]{done} done[/]");
                    if (errored > 0) parts.Add($"[red]{errored} error[/]");

                    agentLabel = $"Check agents ({Markup.Remove(string.Join(", ", parts))})";
                    choices.Add(agentLabel);
                }

                // Group choices
                choices.AddRange(
                    groups.Select(g => $"{FormatType(g.Type)} / {g.Status} ({g.Items.Count})"));

                var exitLabel = "Done reviewing";
                choices.Add(exitLabel);

                // Title with agent status
                var titleParts = new List<string> { $"{availableItems.Count} unresolved item(s)" };
                if (activeAgents.Count > 0)
                {
                    var running = activeAgents.Count(a => a.State == AgentState.Running);
                    if (running > 0)
                        titleParts.Add($"[yellow]{running} agent(s) working[/]");
                }

                var selectedChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"[bold]Review[/] — {string.Join(" · ", titleParts)}. Select:")
                        .HighlightStyle(new Style(Color.Cyan1))
                        .PageSize(15)
                        .AddChoices(choices));

                if (selectedChoice == exitLabel)
                {
                    // Wait for running agents before exiting
                    var running = agents.Where(a => a.State == AgentState.Running).ToList();
                    if (running.Count > 0)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]Waiting for {running.Count} running agent(s) to finish...[/]");
                        await Task.WhenAll(running.Select(a => a.WaitAsync()));
                        await ShowAgentResults(agents);
                    }

                    AnsiConsole.MarkupLine("[dim]Review complete.[/]");
                    return 0;
                }

                if (selectedChoice == agentLabel)
                {
                    await ShowAgentResults(agents);
                    continue;
                }

                // Find which group was selected
                var groupIdx = choices.IndexOf(selectedChoice) - (agentLabel is not null ? 1 : 0);
                if (groupIdx < 0 || groupIdx >= groups.Count)
                    continue;
                var group = groups[groupIdx];

                // Step 2: Pick items within the group
                AnsiConsole.Clear();
                AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(FormatType(group.Type))} / {Markup.Escape(group.Status)}[/]")
                {
                    Justification = Justify.Left,
                });
                AnsiConsole.WriteLine();

                var itemChoices = group.Items
                    .Select(i => $"{Markup.Escape(i.Id)}: {Markup.Escape(Truncate(i.Summary ?? i.Title, 90))}")
                    .ToList();

                var selectedLabels = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("Select items (space = toggle, enter = confirm, none = go back):")
                        .NotRequired()
                        .HighlightStyle(new Style(Color.Cyan1))
                        .PageSize(20)
                        .AddChoices(itemChoices));

                if (selectedLabels.Count == 0)
                    continue;

                var selectedSet = selectedLabels.ToHashSet();
                var selectedItems = group.Items
                    .Where((_, idx) => selectedSet.Contains(itemChoices[idx]))
                    .ToList();

                // Show selected and ask for instruction
                AnsiConsole.WriteLine();
                foreach (var item in selectedItems)
                    AnsiConsole.MarkupLine($"  [bold]•[/] {Markup.Escape(item.Id)}: {Markup.Escape(Truncate(item.Summary ?? item.Title, 80))}");
                AnsiConsole.WriteLine();

                var instruction = AnsiConsole.Ask<string>("[bold]What should I do with these?[/]");

                if (string.IsNullOrWhiteSpace(instruction))
                    continue;

                if (adapterFactory is null)
                {
                    AnsiConsole.MarkupLine("[red]AI adapter required for review actions.[/]");
                    continue;
                }

                // Dispatch as background agent
                var agent = BackgroundAgent.Start(
                    config, store, selectedItems, instruction, adapterFactory);
                agents.Add(agent);

                AnsiConsole.MarkupLine(
                    $"[green]✓ Agent dispatched:[/] [dim]{Markup.Escape(agent.Label)}[/]");
                AnsiConsole.MarkupLine("[dim]Continue reviewing — check results anytime from the menu.[/]");
                await Task.Delay(800);
            }
        }
        finally
        {
            // Dispose all agents on exit
            foreach (var a in agents)
                await a.DisposeAsync();
        }
    }

    /// <summary>
    /// Shows completed/running agents and lets the user interact with finished ones.
    /// </summary>
    private static async Task ShowAgentResults(List<BackgroundAgent> agents)
    {
        while (true)
        {
            var visible = agents.Where(a => a.State != AgentState.Dismissed).ToList();
            if (visible.Count == 0)
                return;

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]Background Agents[/]") { Justification = Justify.Left });
            AnsiConsole.WriteLine();

            var backLabel = "← Back to review";
            var choices = new List<string>();

            foreach (var agent in visible)
            {
                var icon = agent.State switch
                {
                    AgentState.Running => "⟳",
                    AgentState.Done => "✓",
                    AgentState.Error => "✗",
                    _ => "?",
                };
                var stateTag = agent.State switch
                {
                    AgentState.Running => "running",
                    AgentState.Done => "done",
                    AgentState.Error => "error",
                    _ => "unknown",
                };
                choices.Add($"{icon} {agent.Label} ({stateTag})");
            }
            choices.Add(backLabel);

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an agent to view results or send follow-ups:")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .PageSize(15)
                    .AddChoices(choices));

            if (selected == backLabel)
                return;

            var agentIdx = choices.IndexOf(selected);
            if (agentIdx < 0 || agentIdx >= visible.Count)
                continue;

            var picked = visible[agentIdx];

            if (picked.State == AgentState.Running)
            {
                AnsiConsole.MarkupLine("[yellow]This agent is still working. Check back later.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to go back.[/]");
                Console.ReadKey(intercept: true);
                continue;
            }

            // Show the latest response
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(picked.Label)}[/]") { Justification = Justify.Left });
            AnsiConsole.WriteLine();

            foreach (var item in picked.Items)
                AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape(item.Id)}");
            AnsiConsole.WriteLine();

            if (picked.State == AgentState.Error)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(picked.ErrorMessage ?? "Unknown")}[/]");

                var errorAction = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("What would you like to do?")
                        .AddChoices("Dismiss", "← Back"));
                if (errorAction == "Dismiss")
                    picked.Dismiss();
                continue;
            }

            ShowResponse(picked.LastResponse ?? "(no response)");

            // Follow-up / dismiss loop
            while (true)
            {
                AnsiConsole.WriteLine();
                var followUp = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Follow-up[/] [dim](empty to go back, 'done' to dismiss)[/]:")
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(followUp))
                    break;

                if (followUp.Equals("done", StringComparison.OrdinalIgnoreCase))
                {
                    picked.Dismiss();
                    AnsiConsole.MarkupLine("[dim]Agent dismissed.[/]");
                    await Task.Delay(400);
                    break;
                }

                picked.FollowUpInBackground(followUp);
                AnsiConsole.MarkupLine("[dim]Follow-up dispatched — agent is working in the background.[/]");
                await Task.Delay(400);
                break; // return to agent list / review loop
            }
        }
    }

    private static void ShowResponse(string response)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(Markup.Escape(response))
        {
            Header = new PanelHeader("[green]Agent Response[/]"),
            Border = BoxBorder.Double,
            Padding = new Padding(1, 1),
        });
    }

    internal static string BuildAgentPrompt(List<WorkItem> items, string instruction)
    {
        var itemContext = string.Join("\n\n", items.Select(item =>
        {
            var sig = item.Signals.FirstOrDefault();
            var failureDetails = sig?.Metadata?.GetValueOrDefault("failureDetails") ?? "";
            var detailsBlock = string.IsNullOrWhiteSpace(failureDetails)
                ? ""
                : $"\n  Failure details:\n  {failureDetails.Replace("\n", "\n  ")}";

            return $"""
                - **{item.Id}**
                  Type: {sig?.Type ?? "(unknown)"}
                  Status: {item.Status}
                  Title: {item.Title}
                  Summary: {item.Summary ?? "(none)"}
                  Ref: {sig?.Ref ?? "(none)"}
                  Links: {(item.LinkedItems.Count > 0 ? string.Join(", ", item.LinkedItems) : "(none)")}{detailsBlock}
                """;
        }));

        return $"""
            You are a build-duty assistant helping an engineer review work items.
            This is a multi-turn conversation — the engineer may give follow-up
            instructions after your initial response.

            You have tools to:
            - `resolve_work_item(id, reason)` — resolve items
            - `update_work_item_status(id, status)` — change status
            - `link_work_items(id, linkedId)` — link related items
            - `get_task_log(buildUrl, logId, tailLines?)` — fetch build logs
            - `set_work_item_summary(id, summary)` — update summaries

            Use the diagnose-build-break and suggest-next-actions skills if the
            instruction requires investigation or analysis.

            **Selected work items ({items.Count}):**
            {itemContext}

            **Engineer's instruction:**
            > {instruction}

            Execute the instruction. Provide a brief summary of what you did.
            If you need input, clearly ask.
            """;
    }

    private static string FormatType(string type) => type switch
    {
        "ado-pipeline-run" => "Pipeline Failures",
        "github-issue" => "GitHub Issues",
        "github-pr" => "GitHub PRs",
        _ => type,
    };

    private static string Truncate(string text, int max)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= max ? singleLine : singleLine[..(max - 3)] + "...";
    }

    private static string ExtractOrgName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.Trim('/').Split('/')[0];
        return url;
    }

    private sealed record ItemGroup(string Type, string Status, List<WorkItem> Items);
}

/// <summary>
/// An agent that runs in the background, processing a user instruction
/// against a set of work items. The session stays alive so follow-up
/// messages can be sent once the initial work completes.
/// </summary>
internal sealed class BackgroundAgent : IAsyncDisposable
{
    public string Label { get; }
    public List<WorkItem> Items { get; }
    public AgentState State { get; private set; } = AgentState.Running;
    public string? LastResponse { get; private set; }
    public string? ErrorMessage { get; private set; }

    private CopilotAdapter? _adapter;
    private ReviewSession? _session;
    private Task? _workTask;

    private BackgroundAgent(List<WorkItem> items, string instruction)
    {
        Items = items;
        Label = items.Count == 1
            ? $"{items[0].Id}: {Truncate(instruction, 40)}"
            : $"{items.Count} items: {Truncate(instruction, 40)}";
    }

    public static BackgroundAgent Start(
        BuildDutyConfig config,
        WorkItemStore store,
        List<WorkItem> items,
        string instruction,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory)
    {
        var agent = new BackgroundAgent(items, instruction);
        agent._workTask = agent.RunAsync(config, store, instruction, adapterFactory);
        return agent;
    }

    public Task WaitAsync() => _workTask ?? Task.CompletedTask;

    public void Dismiss() => State = AgentState.Dismissed;

    public async Task<string> FollowUpAsync(string prompt)
    {
        if (_session is null)
            throw new InvalidOperationException("Agent session is not available.");

        var response = await _session.SendAsync(prompt);
        LastResponse = response;
        return response;
    }

    /// <summary>
    /// Dispatch a follow-up in the background so the caller can return
    /// to the review loop immediately.
    /// </summary>
    public void FollowUpInBackground(string prompt)
    {
        if (_session is null)
            throw new InvalidOperationException("Agent session is not available.");

        State = AgentState.Running;
        _workTask = Task.Run(async () =>
        {
            try
            {
                LastResponse = await _session.SendAsync(prompt);
                State = AgentState.Done;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                State = AgentState.Error;
            }
        });
    }

    private async Task RunAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        string instruction,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter> adapterFactory)
    {
        try
        {
            _adapter = adapterFactory(config, store);

            var adoOrgUrl = config.AzureDevOps?.Organizations.FirstOrDefault()?.Url;
            var mcpServers = adoOrgUrl is not null
                ? CopilotSessionFactory.AdoPipelineServers(ExtractOrgName(adoOrgUrl))
                : CopilotSessionFactory.NoExtraServers();

            _session = await _adapter.CreateReviewSessionAsync(
                [
                    "skills/triage-signals",
                    "skills/diagnose-build-break",
                    "skills/suggest-next-actions",
                ],
                mcpServers);

            var prompt = ReviewCommand.BuildAgentPrompt(Items, instruction);
            LastResponse = await _session.SendAsync(prompt);
            State = AgentState.Done;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = AgentState.Error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        if (_adapter is not null)
            await _adapter.DisposeAsync();
    }

    private static string Truncate(string text, int max)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= max ? singleLine : singleLine[..(max - 3)] + "...";
    }

    private static string ExtractOrgName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.Trim('/').Split('/')[0];
        return url;
    }
}

internal enum AgentState
{
    Running,
    Done,
    Error,
    Dismissed,
}
