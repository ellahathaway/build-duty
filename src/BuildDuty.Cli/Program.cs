using BuildDuty.Cli.Commands;
using BuildDuty.Cli.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();

services.AddLogging();
services.AddSignalCollectionServices();
services.AddAiServices();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("build-duty");

    config.AddBranch("triage", triage =>
    {
        triage.SetDescription("Collect and triage signals and work items");

        triage.AddCommand<TriageRunCommand>("run")
            .WithDescription("Run a triage process to analyze signals and update work items");

        triage.AddCommand<TriageListCommand>("list")
            .WithDescription("List all triage runs");

        triage.AddCommand<TriageShowCommand>("show")
            .WithDescription("Show details of a specific triage run");
    });

    config.AddCommand<ReviewCommand>("review")
        .WithDescription("Interactively review triaged work items and their signals");

    config.AddBranch("workitem", wi =>
    {
        wi.SetDescription("Manage tracked work items");

        wi.AddCommand<WorkItemsListCommand>("list")
            .WithDescription("List tracked work items");

        wi.AddCommand<WorkItemsShowCommand>("show")
            .WithDescription("Inspect a single work item");
    });

});

return await app.RunAsync(args);
