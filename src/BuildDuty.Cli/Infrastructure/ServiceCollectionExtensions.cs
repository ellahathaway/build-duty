using BuildDuty.Core;
using BuildDuty.Core.Models;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Cli.Infrastructure;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSignalCollectionServices(this IServiceCollection services)
    {
        services
            .AddAzureDevOpsSignalCollection()
            .AddGitHubSignalCollection()
            .AddWorkItemsProvider();

        services.TryAddSingleton<IRemoteTokenProvider>(sp => new RemoteTokenProvider(
            sp.GetRequiredKeyedService<IRemoteTokenProvider>("azdo"),
            sp.GetRequiredKeyedService<IRemoteTokenProvider>("github")));

        services.AddSingleton<ISignalCollectorFactory, SignalCollectorFactory>();

        return services;
    }

    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        // services.AddSingleton<Func<BuildDutyConfig, WorkItemStore, CopilotAdapter>>(_ =>
        // {
        //     return (config, wiStore) =>
        //     {
        //         var tools = BuildDutyTools.Create(wiStore)
        //             .Concat(TriageTools.Create(wiStore))
        //             .Concat(WorkItemTriageTools.Create(wiStore))
        //             .Concat(SummarizeTools.Create(wiStore))
        //             .ToList();

        //         return new CopilotAdapter(new CopilotClientOptions(), tools, config.Ai?.Model);
        //     };
        // });

        return services;
    }

    private static IServiceCollection AddAzureDevOpsSignalCollection(this IServiceCollection services)
    {
        services.TryAddSingleton<ReleaseBranchResolver>();

        services.TryAddKeyedSingleton<IRemoteTokenProvider>("azdo", (_, _) =>
        {
            var options = new AzureDevOpsTokenProviderOptions
            {
                ["default"] = new AzureDevOpsCredentialResolverOptions()
            };
            return AzureDevOpsTokenProvider.FromStaticOptions(options);
        });

        services.AddSingleton<Func<AzureDevOpsConfig, AzureDevOpsSignalCollector>>(sp =>
            config => ActivatorUtilities.CreateInstance<AzureDevOpsSignalCollector>(sp, config));

        return services;
    }

    private static IServiceCollection AddGitHubSignalCollection(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessManager>(sp =>
            new ProcessManager(sp.GetRequiredService<ILogger<ProcessManager>>(), "git"));
        services.TryAddSingleton<GitHubTokenProvider>();
        services.TryAddKeyedSingleton<IRemoteTokenProvider>("github", (sp, _) =>
            sp.GetRequiredService<GitHubTokenProvider>());

        services.AddSingleton<Func<GitHubConfig, GitHubSignalCollector>>(sp =>
            config => ActivatorUtilities.CreateInstance<GitHubSignalCollector>(sp, config));

        return services;
    }

    private static IServiceCollection AddWorkItemsProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IBuildDutyConfigProvider, BuildDutyConfigProvider>();
        services.TryAddSingleton<IWorkItemsProvider, WorkItemsProvider>();
        return services;
    }
}
