using BuildDuty.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("build-duty");

    config.AddCommand<ScanCommand>("scan")
        .WithDescription("Collect configured signals and create/update work items");

    config.AddBranch("workitems", wi =>
    {
        wi.SetDescription("Manage tracked work items");

        wi.AddCommand<WorkItemsListCommand>("list")
            .WithDescription("List tracked work items");

        wi.AddCommand<WorkItemsShowCommand>("show")
            .WithDescription("Inspect a single work item");
    });

    config.AddBranch("ai", ai =>
    {
        ai.SetDescription("AI-assisted analysis");

        ai.AddCommand<AiRunCommand>("run")
            .WithDescription("Run an AI job against work item(s)");
    });
});

return await app.RunAsync(args);
