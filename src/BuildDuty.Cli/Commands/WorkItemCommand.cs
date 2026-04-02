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
	private readonly IStorageProvider _storageProvider;

	public WorkItemsListCommand(
		IBuildDutyConfigProvider configProvider,
		IStorageProvider storageProvider)
		: base(configProvider)
	{
		_storageProvider = storageProvider;
	}

	protected override async Task<int> ExecuteCommandAsync(CommandContext context, WorkItemsListSettings settings)
	{
		var items = (await _storageProvider.GetWorkItemsAsync())
			.OrderByDescending(item => item.UpdatedAt)
			.ToList();

		if (!settings.ShowResolved)
		{
			items = items.Where(item => !item.Resolved).ToList();
		}

		if (settings.Limit is int limit && limit > 0)
		{
			items = items.Take(limit).ToList();
		}

		if (items.Count == 0)
		{
			AnsiConsole.MarkupLine("[yellow]No work items found.[/]");
			return 0;
		}

		var table = new Table().Border(TableBorder.Rounded);
		table.AddColumn("ID");
		table.AddColumn("Resolved");
		table.AddColumn("Summary");
		table.AddColumn("Updated");

		foreach (var item in items)
		{
			table.AddRow(
				Markup.Escape(item.Id),
				item.Resolved ? "[green]yes[/]" : "[yellow]no[/]",
				Markup.Escape(item.Summary ?? "(none)"),
				item.UpdatedAt.ToString("u"));
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
	private readonly IStorageProvider _storageProvider;

	public WorkItemsShowCommand(
		IBuildDutyConfigProvider configProvider,
		IStorageProvider storageProvider)
		: base(configProvider)
	{
		_storageProvider = storageProvider;
	}

	protected override async Task<int> ExecuteCommandAsync(CommandContext context, WorkItemsShowSettings settings)
	{
		WorkItem item;
		try
		{
			item = await _storageProvider.GetWorkItemAsync(settings.Id);
		}
		catch (FileNotFoundException)
		{
			AnsiConsole.MarkupLine($"[red]Work item '{Markup.Escape(settings.Id)}' not found.[/]");
			return 1;
		}

		var panel = new Panel(
			new Rows(
				new Markup($"[bold]ID:[/] {Markup.Escape(item.Id)}"),
				new Markup($"[bold]Resolved:[/] {(item.Resolved ? "yes" : "no")}"),
				new Markup($"[bold]Summary:[/] {Markup.Escape(item.Summary ?? "(none)")}"),
				new Markup($"[bold]Issue Signature:[/] {Markup.Escape(item.IssueSignature ?? "(none)")}"),
				new Markup($"[bold]Correlation Rationale:[/] {Markup.Escape(item.CorrelationRationale ?? "(none)")}"),
				new Markup($"[bold]Resolution Criteria:[/] {Markup.Escape(item.ResolutionCriteria ?? "(none)")}"),
				new Markup($"[bold]Resolution Reason:[/] {Markup.Escape(item.ResolutionReason ?? "(none)")}"),
				new Markup($"[bold]Signals:[/] {(item.SignalIds.Count > 0 ? Markup.Escape(string.Join(", ", item.SignalIds)) : "(none)")}"),
				new Markup($"[bold]Created:[/] {item.CreatedAt:u}"),
				new Markup($"[bold]Updated:[/] {item.UpdatedAt:u}"),
				new Markup($"[bold]Resolved At:[/] {(item.ResolvedAt.HasValue ? item.ResolvedAt.Value.ToString("u") : "(none)")}"),
				new Markup($"[bold]Triage ID:[/] {Markup.Escape(item.TriageId ?? "(none)")}")))
			.Header("[bold blue]Work Item[/]")
			.Border(BoxBorder.Rounded);

		AnsiConsole.Write(panel);
		return 0;
	}
}
