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

    config.AddCommand<TriageCommand>("triage")
        .WithDescription("Gather work items, triage with AI, and correlate results");

    // config.AddCommand<ReviewCommand>("review")
    //     .WithDescription("Interactively review and act on triaged work items");

    // config.AddBranch("workitems", wi =>
    // {
    //     wi.SetDescription("Manage tracked work items");

    //     wi.AddCommand<WorkItemsListCommand>("list")
    //         .WithDescription("List tracked work items");

    //     wi.AddCommand<WorkItemsShowCommand>("show")
    //         .WithDescription("Inspect a single work item");
    // });

});

return await app.RunAsync(args);
