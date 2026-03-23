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

        return await RunReviewAsync(config, store, _adapterFactory);
    }

    public static async Task<int> RunReviewAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>? adapterFactory = null)
    {
        while (true)
        {
            AnsiConsole.Clear();

            var items = await store.ListAsync(resolved: false);
            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No unresolved work items to review.[/]");
                return 0;
            }

            // Group by type then status
            var groups = items
                .GroupBy(i => i.Signals.FirstOrDefault()?.Type ?? "(unknown)")
                .OrderBy(g => g.Key)
                .SelectMany(typeGroup => typeGroup
                    .GroupBy(i => i.Status)
                    .OrderBy(sg => sg.Key)
                    .Select(sg => new ItemGroup(typeGroup.Key, sg.Key, sg.ToList())))
                .Where(g => g.Items.Count > 0)
                .ToList();

            // Step 1: Pick a group
            var exitLabel = "Done reviewing";
            var groupChoices = groups
                .Select(g => $"{FormatType(g.Type)} / {g.Status} ({g.Items.Count})")
                .Append(exitLabel)
                .ToList();

            var selectedGroup = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[bold]Review[/] — {items.Count} unresolved item(s). Select a group:")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .PageSize(15)
                    .AddChoices(groupChoices));

            if (selectedGroup == exitLabel)
            {
                AnsiConsole.MarkupLine("[dim]Review complete.[/]");
                return 0;
            }

            var groupIndex = groupChoices.IndexOf(selectedGroup);
            var group = groups[groupIndex];

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

            // Multi-turn conversation with the agent on these items
            await RunConversationAsync(config, store, selectedItems, instruction, adapterFactory);
        }
    }

    /// <summary>
    /// Opens a multi-turn conversation with the AI agent for the selected items.
    /// The first message includes full item context. Follow-ups are sent on the
    /// same session so the agent retains memory.
    /// </summary>
    private static async Task RunConversationAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        List<WorkItem> items,
        string firstInstruction,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>? adapterFactory)
    {
        if (adapterFactory is null)
        {
            AnsiConsole.MarkupLine("[red]AI adapter required for review actions.[/]");
            return;
        }

        var adoOrgUrl = config.AzureDevOps?.Organizations.FirstOrDefault()?.Url;
        var mcpServers = adoOrgUrl is not null
            ? CopilotSessionFactory.AdoPipelineServers(ExtractOrgName(adoOrgUrl))
            : CopilotSessionFactory.NoExtraServers();

        await using var adapter = adapterFactory(config, store);
        ReviewSession session;

        try
        {
            session = await adapter.CreateReviewSessionAsync(
                [
                    "skills/triage-signals",
                    "skills/diagnose-build-break",
                    "skills/suggest-next-actions",
                ],
                mcpServers);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start agent: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        await using (session)
        {
            // Build the initial prompt with full item context
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

            var firstPrompt = $"""
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
                > {firstInstruction}

                Execute the instruction. Provide a brief summary of what you did.
                If you need input, clearly ask.
                """;

            // Send first message
            var response = await SendWithSpinner(session, firstPrompt);
            ShowResponse(response);

            // Multi-turn loop
            while (true)
            {
                AnsiConsole.WriteLine();
                var followUp = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Follow-up[/] [dim](empty to go back)[/]:")
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(followUp))
                    break;

                response = await SendWithSpinner(session, followUp);
                ShowResponse(response);
            }
        }
    }

    private static async Task<string> SendWithSpinner(ReviewSession session, string prompt)
    {
        string? response = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Agent working...[/]", async _ =>
            {
                try
                {
                    response = await session.SendAsync(prompt);
                }
                catch (Exception ex)
                {
                    response = $"Error: {ex.Message}";
                }
            });

        return response ?? "(no response)";
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
