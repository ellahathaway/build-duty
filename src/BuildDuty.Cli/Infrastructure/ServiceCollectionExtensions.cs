using BuildDuty.Core;
using BuildDuty.AI;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Cli.Infrastructure;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBuildDutyConfigProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IBuildDutyConfigProvider, BuildDutyConfigProvider>();
        return services;
    }

    public static IServiceCollection AddSignalCollectionServices(this IServiceCollection services)
    {
        services
            .AddBuildDutyConfigProvider()
            .AddStorageProvider()
            .AddRemoteTokenProvider();

        services.TryAddTransient<GitHubSignalCollector>();
        services.TryAddSingleton<ReleaseBranchResolver>();
        services.TryAddTransient<AzureDevOpsSignalCollector>();
        services.AddSingleton<ISignalCollectorFactory, SignalCollectorFactory>();

        return services;
    }

    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        services.AddBuildDutyConfigProvider();
        services.AddStorageProvider();
        services.TryAddSingleton<StorageTools>();
        services.TryAddSingleton<CopilotAdapter>();
        return services;
    }

    private static IServiceCollection AddStorageProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageProvider, StorageProvider>();
        return services;
    }

    private static IServiceCollection AddRemoteTokenProvider(this IServiceCollection services)
    {
        // Azure DevOps token provider
        services.TryAddKeyedSingleton<IRemoteTokenProvider>("azdo", (_, _) =>
        {
            var options = new AzureDevOpsTokenProviderOptions
            {
                ["default"] = new AzureDevOpsCredentialResolverOptions()
            };
            return AzureDevOpsTokenProvider.FromStaticOptions(options);
        });

        // GitHub token provider
        services.TryAddSingleton<IProcessManager>(sp =>
            new ProcessManager(sp.GetRequiredService<ILogger<ProcessManager>>(), "git"));
        services.TryAddSingleton<GitHubTokenProvider>();
        services.TryAddKeyedSingleton<IRemoteTokenProvider>("github", (sp, _) =>
            sp.GetRequiredService<GitHubTokenProvider>());

        return services;
    }
}
