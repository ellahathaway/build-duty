using BuildDuty.AI;
using BuildDuty.Cli;
using BuildDuty.Cli.Commands;
using BuildDuty.Cli.Infrastructure;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.DependencyInjection;
using Octokit;
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

// GitHub
services.AddSingleton<IGitHubCredentialProvider>(_ => GitHubCredentialProvider.Create());
services.AddSingleton<IGitHubClient>(sp =>
{
    var creds = sp.GetRequiredService<IGitHubCredentialProvider>().GetCredentials();
    return new GitHubClient(new Octokit.ProductHeaderValue("build-duty")) { Credentials = creds };
});
services.AddSingleton<Func<GitHubConfig, IGitHubSignalService>>(sp =>
    config => new GitHubSignalService(config, sp.GetRequiredService<IGitHubClient>()));

// Config + store factories — config path is resolved at command time, so we
// register factories that commands invoke with the loaded config name.
services.AddSingleton<Func<string, WorkItemStore>>(
    _ => (string configName) => new WorkItemStore(Paths.WorkItemsDir(configName)));
services.AddSingleton<Func<string, AiRunStore>>(
    _ => (string configName) => new AiRunStore(Paths.AiRunsDir(configName)));

// AI — adapter factory builds a CopilotClient with tools at command time.
// MCP servers are loaded from mcp.json by the session factory.
services.AddSingleton<Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>>(sp =>
{
    var ghCredProvider = sp.GetRequiredService<IGitHubCredentialProvider>();
    return (config, wiStore) =>
    {
        var token = ghCredProvider.GetToken();
        var tools = BuildDutyTools.Create(wiStore);

        var clientOptions = new CopilotClientOptions
        {
            GitHubToken = token
        };

        return new CopilotAdapter(clientOptions, tools, config.Ai?.Model);
    };
});

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

    config.AddCommand<AiCommand>("ai")
        .WithDescription("Run an AI job against work item(s)");
});

return await app.RunAsync(args);
