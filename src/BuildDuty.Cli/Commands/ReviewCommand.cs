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

    /// <summary>
    /// Interactive review loop — callable from both the standalone command
    /// and from the end of the triage command.
    /// </summary>
    public static async Task<int> RunReviewAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>? adapterFactory = null)
    {
        var firstPass = true;

        while (true)
        {
            var items = await store.ListAsync(resolved: false);
            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No unresolved work items to review.[/]");
                return 0;
            }

            if (!firstPass)
                AnsiConsole.Clear();
            firstPass = false;

            // Group by type → status
            var groups = items
                .GroupBy(i => i.Signals.FirstOrDefault()?.Type ?? "(unknown)")
                .OrderBy(g => g.Key)
                .SelectMany(typeGroup => typeGroup
                    .GroupBy(i => i.Status)
                    .OrderBy(sg => sg.Key)
                    .Select(sg => new ItemGroup(typeGroup.Key, sg.Key, sg.ToList())))
                .Where(g => g.Items.Count > 0)
                .ToList();

            // Render summary table
            var rule = new Rule($"[bold]Review[/] — {items.Count} unresolved item(s)") { Justification = Justify.Left };
            AnsiConsole.Write(rule);
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Group");
            table.AddColumn("ID");
            table.AddColumn("Summary");

            var allChoices = new List<(string Label, WorkItem Item)>();
            foreach (var group in groups)
            {
                var groupTag = $"{FormatType(group.Type)} / {group.Status}";
                var isFirst = true;

                foreach (var item in group.Items)
                {
                    var summary = Truncate(item.Summary ?? item.Title, 80);
                    var choiceLabel = $"{Markup.Escape(item.Id)}: {Markup.Escape(summary)}";
                    allChoices.Add((choiceLabel, item));

                    table.AddRow(
                        isFirst ? Markup.Escape(groupTag) : "",
                        Markup.Escape(item.Id),
                        Markup.Escape(summary));
                    isFirst = false;
                }

                table.AddEmptyRow();
            }

            AnsiConsole.Write(table);

            // Multi-select
            var selectLabels = allChoices.Select(c => c.Label).ToList();
            var selected = AnsiConsole.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title("[bold]Select items[/] (space = toggle, enter = confirm, none = exit):")
                    .NotRequired()
                    .PageSize(20)
                    .UseConverter(s => s)  // labels are already escaped
                    .AddChoices(selectLabels));

            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("\n[dim]Review complete.[/]");
                return 0;
            }

            // Map selections back to work items
            var selectedSet = selected.ToHashSet();
            var selectedItems = allChoices
                .Where(c => selectedSet.Contains(c.Label))
                .Select(c => c.Item)
                .ToList();

            // Show selected
            AnsiConsole.WriteLine();
            var selectedPanel = new Panel(
                string.Join("\n", selectedItems.Select(i =>
                    $"• {i.Id}: {Truncate(i.Summary ?? i.Title, 70)}")))
            {
                Header = new PanelHeader($"{selectedItems.Count} item(s) selected"),
                Border = BoxBorder.Rounded,
            };
            AnsiConsole.Write(selectedPanel);

            // Freeform instruction
            var instruction = AnsiConsole.Ask<string>("\n[bold]What should I do with these?[/]");

            if (string.IsNullOrWhiteSpace(instruction))
                continue;

            // Execute via agent
            await ExecuteInstructionAsync(config, store, selectedItems, instruction, adapterFactory);

            // Pause before clearing
            AnsiConsole.MarkupLine("\n[dim]Press enter to continue...[/]");
            Console.ReadLine();
        }
    }

    private static async Task ExecuteInstructionAsync(
        BuildDutyConfig config,
        WorkItemStore store,
        List<WorkItem> items,
        string instruction,
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

        var prompt = $"""
            You are a build-duty assistant helping an engineer review work items.

            The engineer selected the following {items.Count} work item(s) and gave
            this instruction:

            > {instruction}

            **Selected work items:**
            {itemContext}

            Execute the engineer's instruction. You have tools to:
            - `resolve_work_item(id, reason)` — resolve items
            - `update_work_item_status(id, status)` — change status
            - `link_work_items(id, linkedId)` — link related items
            - `get_task_log(buildUrl, logId, tailLines?)` — fetch build logs
            - `set_work_item_summary(id, summary)` — update summaries

            Use the diagnose-build-break and suggest-next-actions skills if the
            instruction requires investigation or analysis.

            After executing, provide a brief summary of what you did. If you need
            the engineer's input to proceed, clearly ask your question and explain
            what you need to know.
            """;

        string? response = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Agent working...[/]", async _ =>
            {
                await using var adapter = adapterFactory(config, store);
                try
                {
                    var result = await adapter.ScanSourceAsync(
                        prompt, "review",
                        [
                            "skills/triage-signals",
                            "skills/diagnose-build-break",
                            "skills/suggest-next-actions",
                        ],
                        mcpServers, ct: default);

                    response = result.Summary;
                }
                catch (Exception ex)
                {
                    response = $"Error: {ex.Message}";
                }
            });

        if (response is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(Markup.Escape(response))
            {
                Header = new PanelHeader("[green]Agent Response[/]"),
                Border = BoxBorder.Double,
                Padding = new Padding(1, 1),
            });
        }
        else
        {
            AnsiConsole.MarkupLine("[red]No response from agent.[/]");
        }
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
