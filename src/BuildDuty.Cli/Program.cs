using BuildDuty.AI;
using BuildDuty.Cli;
using BuildDuty.Cli.Commands;
using BuildDuty.Cli.Infrastructure;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();

// Config + store factories — config path is resolved at command time, so we
// register factories that commands invoke with the loaded config name.
services.AddSingleton<Func<string, string?, WorkItemStore>>(
    _ => (string configName, string? configPath) => new WorkItemStore(Paths.WorkItemsDir(configName, configPath)));
services.AddSingleton<Func<string, string?, TriageStore>>(
    _ => (string configName, string? configPath) => new TriageStore(Paths.TriageRunsDir(configName, configPath)));

// AI — adapter factory builds a CopilotClient with tools at command time.
services.AddSingleton<Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>>(_ =>
{
    return (config, wiStore) =>
    {
        var tools = BuildDutyTools.Create(wiStore)
            .Concat(TriageTools.Create(wiStore))
            .Concat(WorkItemTriageTools.Create(wiStore))
            .Concat(SummarizeTools.Create(wiStore))
            .ToList();

        return new CopilotAdapter(new CopilotClientOptions(), tools, config.Ai?.Model);
    };
});

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("build-duty");

    config.AddCommand<TriageCommand>("triage")
        .WithDescription("Gather work items, triage with AI, and correlate results");

    config.AddCommand<ReviewCommand>("review")
        .WithDescription("Interactively review and act on triaged work items");

    config.AddBranch("workitems", wi =>
    {
        wi.SetDescription("Manage tracked work items");

        wi.AddCommand<WorkItemsListCommand>("list")
            .WithDescription("List tracked work items");

        wi.AddCommand<WorkItemsShowCommand>("show")
            .WithDescription("Inspect a single work item");

        wi.AddCommand<WorkItemRunCommand>("run")
            .WithDescription("Run an AI action against work item(s) (e.g. 'review this PR', 'root cause analysis')");
    });
});

return await app.RunAsync(args);
