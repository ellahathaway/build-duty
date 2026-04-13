using System.ComponentModel;
using BuildDuty.Core;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

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

	[CommandOption("--verbose")]
	[Description("Show verbose signal output")]
	public bool Verbose { get; set; }

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

		var verboseSignalMarkUpTasks = settings.Verbose
			? item.LinkedAnalyses.Select(async la =>
			{
				var signal = await _storageProvider.GetSignalAsync(la.SignalId);
				string link = signal.Url?.ToString() ?? "(none)";
				var analyses = signal.Analyses.Where(a => la.AnalysisIds.Contains(a.Id)).Select(a => a.Analysis);
				var markups = new List<IRenderable>
				{
					new Markup($"  [bold]Signal:[/] {Markup.Escape(la.SignalId)}"),
					new Markup($"  [bold]Link:[/] {Markup.Escape(link)}"),
				};
				foreach (var analysis in analyses)
				{
					markups.Add(new Markup($"  [bold]Analysis:[/] {Markup.Escape(analysis)}"));
				}
				return markups;
			})
			: null;
		var verboseSignalRenderables = verboseSignalMarkUpTasks is not null
			? (await Task.WhenAll(verboseSignalMarkUpTasks)).SelectMany(m => m).ToList()
			: new List<IRenderable>();

		var rows = new List<IRenderable>
		{
			new Markup($"[bold]ID:[/] {Markup.Escape(item.Id)}"),
			new Markup($"[bold]Resolved:[/] {(item.Resolved ? "yes" : "no")}"),
			new Markup($"[bold]Summary:[/] {Markup.Escape(item.Summary ?? "(none)")}"),
			new Markup($"[bold]Issue Signature:[/] {Markup.Escape(item.IssueSignature ?? "(none)")}"),
			new Markup($"[bold]Signals:[/] {(item.LinkedAnalyses.Count > 0 ? Markup.Escape(string.Join(", ", item.LinkedAnalyses.Select(la => $"{la.SignalId} ({la.AnalysisIds.Count} analyses)"))) : "(none)")}"),
		};
		foreach (var renderable in verboseSignalRenderables)
		{
			rows.Add(renderable);
		}
		rows.Add(new Markup($"[bold]Created:[/] {item.CreatedAt:u}"));
		rows.Add(new Markup($"[bold]Updated:[/] {item.UpdatedAt:u}"));
		rows.Add(new Markup($"[bold]Resolved At:[/] {(item.ResolvedAt.HasValue ? item.ResolvedAt.Value.ToString("u") : "(none)")}"));

		var panel = new Panel(new Rows(rows))
			.Header("[bold blue]Work Item[/]")
			.Border(BoxBorder.Rounded);

		AnsiConsole.Write(panel);
		return 0;
	}
}
