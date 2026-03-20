using System.ComponentModel;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class WorkItemsListSettings : CommandSettings
{
    [CommandOption("--state")]
    [Description("Filter by state (unresolved, inprogress, resolved)")]
    public string? State { get; set; }

    [CommandOption("--limit")]
    [Description("Maximum number of items to display")]
    public int? Limit { get; set; }
}

internal sealed class WorkItemsListCommand : AsyncCommand<WorkItemsListSettings>
{
    private readonly Func<string, WorkItemStore> _storeFactory;

    public WorkItemsListCommand(Func<string, WorkItemStore> storeFactory)
    {
        _storeFactory = storeFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WorkItemsListSettings settings)
    {
        var configPath = Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found.");
        var config = BuildDutyConfig.LoadFromFile(configPath);
        var store = _storeFactory(config.Name);

        WorkItemState? filter = settings.State?.ToLowerInvariant() switch
        {
            "unresolved" => WorkItemState.Unresolved,
            "inprogress" => WorkItemState.InProgress,
            "resolved" => WorkItemState.Resolved,
            null => null,
            _ => throw new ArgumentException($"Unknown state '{settings.State}'. Use: unresolved, inprogress, resolved")
        };

        var items = await store.ListAsync(filter, settings.Limit);

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No work items found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("State");
        table.AddColumn("Title");

        foreach (var item in items)
        {
            var stateMarkup = item.State switch
            {
                WorkItemState.Unresolved => "[red]unresolved[/]",
                WorkItemState.InProgress => "[yellow]inprogress[/]",
                WorkItemState.Resolved => "[green]resolved[/]",
                _ => item.State.ToString()
            };
            table.AddRow(
                Markup.Escape(item.Id),
                stateMarkup,
                Markup.Escape(item.Title));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{items.Count} item(s)[/]");
        return 0;
    }
}

internal sealed class WorkItemsShowSettings : CommandSettings
{
    [CommandOption("--id")]
    [Description("Work item ID")]
    public string Id { get; set; } = string.Empty;

    public override ValidationResult Validate()
    {
        return string.IsNullOrWhiteSpace(Id)
            ? ValidationResult.Error("--id is required")
            : ValidationResult.Success();
    }
}

internal sealed class WorkItemsShowCommand : AsyncCommand<WorkItemsShowSettings>
{
    private readonly Func<string, WorkItemStore> _storeFactory;

    public WorkItemsShowCommand(Func<string, WorkItemStore> storeFactory)
    {
        _storeFactory = storeFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WorkItemsShowSettings settings)
    {
        var configPath = Paths.ConfigPath()
            ?? throw new InvalidOperationException("No .build-duty.yml found.");
        var config = BuildDutyConfig.LoadFromFile(configPath);
        var store = _storeFactory(config.Name);
        var item = await store.LoadAsync(settings.Id);

        if (item is null)
        {
            AnsiConsole.MarkupLine($"[red]Work item '{Markup.Escape(settings.Id)}' not found.[/]");
            return 1;
        }

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]ID:[/]             {Markup.Escape(item.Id)}"),
                new Markup($"[bold]State:[/]          {StateMarkup(item.State)}"),
                new Markup($"[bold]Title:[/]          {Markup.Escape(item.Title)}"),
                new Markup($"[bold]Correlation ID:[/] {Markup.Escape(item.CorrelationId ?? "(none)")}")
            ))
            .Header("[bold blue]Work Item[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        if (item.Signals.Count > 0)
        {
            var sigTable = new Table().Border(TableBorder.Simple).Title("[bold]Signals[/]");
            sigTable.AddColumn("Type");
            sigTable.AddColumn("Ref");
            foreach (var sig in item.Signals)
                sigTable.AddRow(Markup.Escape(sig.Type), Markup.Escape(sig.Ref));
            AnsiConsole.Write(sigTable);
        }

        if (item.History.Count > 0)
        {
            var histTable = new Table().Border(TableBorder.Simple).Title("[bold]History[/]");
            histTable.AddColumn("Timestamp");
            histTable.AddColumn("Action");
            histTable.AddColumn("From → To");
            histTable.AddColumn("Note");
            foreach (var entry in item.History)
            {
                histTable.AddRow(
                    entry.TimestampUtc.ToString("u"),
                    Markup.Escape(entry.Action),
                    $"{Markup.Escape(entry.From ?? "")} → {Markup.Escape(entry.To ?? "")}",
                    Markup.Escape(entry.Note ?? ""));
            }
            AnsiConsole.Write(histTable);
        }

        return 0;
    }

    private static string StateMarkup(WorkItemState state) => state switch
    {
        WorkItemState.Unresolved => "[red]unresolved[/]",
        WorkItemState.InProgress => "[yellow]inprogress[/]",
        WorkItemState.Resolved => "[green]resolved[/]",
        _ => state.ToString()
    };
}
