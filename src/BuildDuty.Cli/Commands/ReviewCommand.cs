using System.ComponentModel;
using System.Text;
using System.Text.Json;
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
                    .Where(i => i.Status != "tracked")
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
                    .GroupBy(i => i.Sources.FirstOrDefault()?.Type ?? "(unknown)")
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
                        await ShowAgentResults(agents, store);
                    }

                    AnsiConsole.MarkupLine("[dim]Review complete.[/]");
                    return 0;
                }

                if (selectedChoice == agentLabel)
                {
                    await ShowAgentResults(agents, store);
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
    /// Shows agents and lets the user interact — watch live, send follow-ups, dismiss.
    /// </summary>
    private static async Task ShowAgentResults(List<BackgroundAgent> agents, WorkItemStore store)
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
                    AgentState.Running => "running — select to watch live",
                    AgentState.Done => "done",
                    AgentState.Error => "error",
                    _ => "unknown",
                };
                choices.Add($"{icon} {agent.Label} ({stateTag})");
            }
            choices.Add(backLabel);

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an agent to interact with:")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .PageSize(15)
                    .AddChoices(choices));

            if (selected == backLabel)
                return;

            var agentIdx = choices.IndexOf(selected);
            if (agentIdx < 0 || agentIdx >= visible.Count)
                continue;

            await InteractWithAgent(visible[agentIdx], store);
        }
    }

    /// <summary>
    /// Full interaction with an agent: live watch (if running), show response, follow-up loop.
    /// </summary>
    private static async Task InteractWithAgent(BackgroundAgent agent, WorkItemStore store)
    {
        // If running, enter live watch mode
        if (agent.State == AgentState.Running)
        {
            var escaped = await WatchAgentLive(agent);
            if (escaped)
            {
                AnsiConsole.MarkupLine("[dim]Agent continues in background.[/]");
                await Task.Delay(500);
                return;
            }
        }

        // Follow-up loop (agent is done or errored)
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(agent.Label)}[/]") { Justification = Justify.Left });
            AnsiConsole.WriteLine();

            foreach (var item in agent.Items)
                AnsiConsole.MarkupLine($"  [dim]•[/] {Markup.Escape(item.Id)}");
            AnsiConsole.WriteLine();

            if (agent.State == AgentState.Error)
            {
                AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(agent.ErrorMessage ?? "Unknown")}[/]");

                var errorAction = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("What would you like to do?")
                        .AddChoices("Dismiss", "← Back"));
                if (errorAction == "Dismiss")
                    await DismissWithStatusPrompt(agent, store);
                return;
            }

            ShowResponse(agent.LastResponse ?? "(no response)");

            AnsiConsole.WriteLine();
            var followUp = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Follow-up[/] [dim](empty to go back, 'done' to dismiss)[/]:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(followUp))
                return;

            if (followUp.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                await DismissWithStatusPrompt(agent, store);
                return;
            }

            // Dispatch follow-up and watch it live
            agent.FollowUpInBackground(followUp);
            var wasEscaped = await WatchAgentLive(agent);
            if (wasEscaped)
            {
                AnsiConsole.MarkupLine("[dim]Agent continues in background.[/]");
                await Task.Delay(500);
                return;
            }
            // Loop back to show new response and prompt again
        }
    }

    /// <summary>
    /// Live-stream agent activity to the terminal. Returns true if user pressed Escape.
    /// </summary>
    private static async Task<bool> WatchAgentLive(BackgroundAgent agent)
    {
        var sb = new StringBuilder();
        var cursor = 0;
        var buffer = new List<AgentStreamEvent>();
        var escaped = false;

        await AnsiConsole.Live(BuildStreamPanel(sb, agent))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                while (agent.State == AgentState.Running)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                        {
                            escaped = true;
                            return;
                        }
                    }

                    cursor = agent.ReadStreamEvents(cursor, buffer);
                    foreach (var evt in buffer)
                        FormatStreamEvent(sb, evt);
                    buffer.Clear();

                    ctx.UpdateTarget(BuildStreamPanel(sb, agent));
                    await Task.Delay(100);
                }

                // Final flush after completion
                cursor = agent.ReadStreamEvents(cursor, buffer);
                foreach (var evt in buffer)
                    FormatStreamEvent(sb, evt);
                buffer.Clear();
                ctx.UpdateTarget(BuildStreamPanel(sb, agent));
            });

        return escaped;
    }

    private static Panel BuildStreamPanel(StringBuilder sb, BackgroundAgent agent)
    {
        var icon = agent.State switch
        {
            AgentState.Running => "⟳",
            AgentState.Done => "✓",
            AgentState.Error => "✗",
            _ => "?",
        };

        var text = sb.Length > 0 ? sb.ToString() : "[dim]Waiting for response...[/]";

        // Show last 50 lines to keep terminal manageable
        var lines = text.Split('\n');
        if (lines.Length > 50)
            text = "[dim]...[/]\n" + string.Join('\n', lines[^50..]);

        var stateHint = agent.State == AgentState.Running ? " [dim](Esc to return to menu)[/]" : "";

        return new Panel(new Markup(text))
        {
            Header = new PanelHeader($"{icon} [bold]{Markup.Escape(agent.Label)}[/]{stateHint}"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0),
        };
    }

    private static void FormatStreamEvent(StringBuilder sb, AgentStreamEvent evt)
    {
        switch (evt.Type)
        {
            case "delta":
                sb.Append(Markup.Escape(evt.Content ?? ""));
                break;
            case "tool-start":
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"  [dim]┌─ 🔧 {Markup.Escape(evt.ToolName ?? "?")}[/]");
                if (!string.IsNullOrEmpty(evt.ToolArgs))
                {
                    foreach (var argLine in FormatToolArgs(evt.ToolArgs))
                        sb.AppendLine($"  [dim]│[/] {argLine}");
                }
                break;
            case "tool-end":
                if (evt.ToolSuccess == true)
                    sb.AppendLine("  [dim]└─[/] [green]✓ done[/]");
                else
                    sb.AppendLine("  [dim]└─[/] [red]✗ failed[/]");
                sb.AppendLine();
                break;
            case "error":
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                sb.AppendLine($"[red]⚠ {Markup.Escape(evt.Content ?? "")}[/]");
                break;
        }
    }

    private static List<string> FormatToolArgs(string argsJson)
    {
        var result = new List<string>();

        // Try to parse as JSON for structured display
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();

                // Truncate very long values (URLs, large content)
                if (val.Length > 100)
                    val = val[..97] + "...";

                result.Add($"  [dim]{Markup.Escape(prop.Name)}:[/] {Markup.Escape(val)}");
            }
        }
        catch
        {
            // Not valid JSON — show raw (truncated)
            var display = argsJson.Length > 120 ? argsJson[..117] + "..." : argsJson;
            result.Add($"  [dim]{Markup.Escape(display)}[/]");
        }

        return result;
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

    /// <summary>
    /// Prompt the user for a status to apply to the agent's items, then dismiss.
    /// Items already in a terminal status are skipped.
    /// </summary>
    private static async Task DismissWithStatusPrompt(BackgroundAgent agent, WorkItemStore store)
    {
        // Check which items still need a status decision
        var actionable = agent.Items.Where(i => !WorkItem.TerminalStatuses.Contains(i.Status)).ToList();

        if (actionable.Count > 0)
        {
            var choices = new List<string>
            {
                "monitoring — watching, may return later",
                "acknowledged — no action needed, ignore unless resolved",
                "needs-investigation — needs deeper analysis",
                "resolved — issue is done",
                "leave as-is",
            };

            // Remove invalid transitions
            if (actionable.Any(i => i.Status == "tracked"))
                choices.Remove("monitoring — watching, may return later");

            var status = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Set status for these items:[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(choices));

            if (status != "leave as-is")
            {
                var newStatus = status.Split('—')[0].Trim();
                foreach (var item in actionable)
                {
                    item.SetStatus(newStatus, $"Set during review dismiss");
                    await store.SaveAsync(item);
                }

                AnsiConsole.MarkupLine($"[dim]{actionable.Count} item(s) → {newStatus}[/]");
            }
        }

        agent.Dismiss();
        AnsiConsole.MarkupLine("[dim]Agent dismissed.[/]");
        await Task.Delay(400);
    }

    internal static string BuildAgentPrompt(List<WorkItem> items, string instruction)
    {
        var itemContext = string.Join("\n\n", items.Select(item =>
        {
            var sourceRef = item.Sources.FirstOrDefault();
            var failureDetails = sourceRef?.Metadata?.GetValueOrDefault("failureDetails") ?? "";
            var detailsBlock = string.IsNullOrWhiteSpace(failureDetails)
                ? ""
                : $"\n  Failure details:\n  {failureDetails.Replace("\n", "\n  ")}";

            return $"""
                - **{item.Id}**
                  Type: {sourceRef?.Type ?? "(unknown)"}
                  Status: {item.Status}
                  Title: {item.Title}
                  Summary: {item.Summary ?? "(none)"}
                  Ref: {sourceRef?.Ref ?? "(none)"}
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

    // Stream event buffer for live watch mode
    private readonly List<AgentStreamEvent> _streamEvents = new();
    private readonly object _streamLock = new();

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

    /// <summary>
    /// Read stream events since the given cursor. Returns the new cursor position.
    /// </summary>
    public int ReadStreamEvents(int cursor, List<AgentStreamEvent> buffer)
    {
        lock (_streamLock)
        {
            for (int i = cursor; i < _streamEvents.Count; i++)
                buffer.Add(_streamEvents[i]);
            return _streamEvents.Count;
        }
    }

    private void OnStreamEvent(AgentStreamEvent evt)
    {
        lock (_streamLock)
        {
            _streamEvents.Add(evt);
        }
    }

    private void ClearStreamBuffer()
    {
        lock (_streamLock)
        {
            _streamEvents.Clear();
        }
    }

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
        ClearStreamBuffer();
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
                    "skills/triage",
                    "skills/diagnose-build-break",
                    "skills/suggest-next-actions",
                ],
                mcpServers);

            _session.OnStream(OnStreamEvent);

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
