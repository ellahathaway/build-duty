using BuildDuty.AI;
using BuildDuty.Cli;
using BuildDuty.Cli.Commands;
using BuildDuty.Cli.Infrastructure;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();

// Credential provider factory — interactive vs CI is a command-time decision.
services.AddSingleton<Func<bool, IAzureDevOpsCredentialProvider>>(_ => ci =>
    ci ? AzureDevOpsCredentialProvider.CreateForCi()
       : AzureDevOpsCredentialProvider.CreateInteractive());

// ADO client factory
services.AddSingleton<Func<bool, IBuildHttpClientFactory>>(sp => ci =>
    new BuildHttpClientFactory(sp.GetRequiredService<Func<bool, IAzureDevOpsCredentialProvider>>()(ci)));

// Signal service factory — config and CI mode are resolved at command time.
services.AddSingleton<Func<AzureDevOpsConfig, bool, IAzureDevOpsSignalService>>(sp =>
    (config, ci) => new AzureDevOpsSignalService(
        config, sp.GetRequiredService<Func<bool, IBuildHttpClientFactory>>()(ci)));

// Config + store factories — config path is resolved at command time, so we
// register factories that commands invoke with the loaded config name.
services.AddSingleton<Func<string, WorkItemStore>>(
    _ => (string configName) => new WorkItemStore(Paths.WorkItemsDir(configName)));
services.AddSingleton<Func<string, AiRunStore>>(
    _ => (string configName) => new AiRunStore(Paths.AiRunsDir(configName)));

// AI components — these are config-independent singletons.
services.AddSingleton(_ => RouterManifest.LoadFromFile(Paths.RouterYamlPath()));
services.AddSingleton<CopilotAdapter>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

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
