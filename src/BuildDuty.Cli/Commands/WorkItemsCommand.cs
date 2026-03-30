using System.ComponentModel;
using BuildDuty.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal sealed class WorkItemsListSettings : BaseSettings
{
    [CommandOption("--limit")]
    [Description("Maximum number of items to display")]
    public int? Limit { get; set; }

    [CommandOption("--show-resolved")]
    [Description("Include resolved work items")]
    public bool ShowResolved { get; set; }
}

internal sealed class WorkItemsListCommand : BaseCommand<WorkItemsListSettings>
{
    private readonly WorkItemStore _store;

    public WorkItemsListCommand(
        IBuildDutyConfigProvider configProvider,
        WorkItemStore store) : base(configProvider)
    {
        _store = store;
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, WorkItemsListSettings settings)
    {
        var items = await _store.ListAsync(
            resolved: settings.ShowResolved ? null : false,
            limit: settings.Limit);

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No work items found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("Status");
        table.AddColumn("Title");

        foreach (var item in items)
        {
            var statusColor = item.Status switch
            {
                "acknowledged" or "monitoring" => "dim",
                "new" => "yellow",
                "needs-investigation" => "red",
                _ when item.IsResolved => "green",
                _ => "red",
            };
            var statusMarkup = $"[{statusColor}]{Markup.Escape(item.Status)}[/]";
            table.AddRow(
                Markup.Escape(item.Id),
                statusMarkup,
                Markup.Escape(item.Title));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{items.Count} item(s)[/]");
        return 0;
    }
}

internal sealed class WorkItemsShowSettings : BaseSettings
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

internal sealed class WorkItemsShowCommand : BaseCommand<WorkItemsShowSettings>
{
    private readonly WorkItemStore _store;

    public WorkItemsShowCommand(
        IBuildDutyConfigProvider configProvider,
        WorkItemStore store) : base(configProvider)
    {
        _store = store;

    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, WorkItemsShowSettings settings)
    {
        var item = await _store.LoadAsync(settings.Id);

        if (item is null)
        {
            AnsiConsole.MarkupLine($"[red]Work item '{Markup.Escape(settings.Id)}' not found.[/]");
            return 1;
        }

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]ID:[/]             {Markup.Escape(item.Id)}"),
                new Markup($"[bold]Status:[/]         {Markup.Escape(item.Status)}"),
                new Markup($"[bold]Title:[/]          {Markup.Escape(item.Title)}"),
                new Markup($"[bold]Correlation ID:[/] {Markup.Escape(item.CorrelationId ?? "(none)")}"),
                new Markup($"[bold]Summary:[/]        {Markup.Escape(item.Summary ?? "(none)")}"),
                new Markup($"[bold]Linked:[/]         {(item.LinkedItems.Count > 0 ? Markup.Escape(string.Join(", ", item.LinkedItems)) : "(none)")}")
            ))
            .Header("[bold blue]Work Item[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        if (item.Sources.Count > 0)
        {
            var srcTable = new Table().Border(TableBorder.Simple).Title("[bold]Sources[/]");
            srcTable.AddColumn("Type");
            srcTable.AddColumn("Ref");
            foreach (var src in item.Sources)
            {
                srcTable.AddRow(Markup.Escape(src.Type), Markup.Escape(src.Ref));
            }

            AnsiConsole.Write(srcTable);
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

}
