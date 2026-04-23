using BuildDuty.AI;
using BuildDuty.AI.Tools;
using BuildDuty.Core;
using GitHub.Copilot.SDK;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;
using Microsoft.DotNet.DarcLib.Helpers;
using Microsoft.Extensions.AI;
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
            .AddGeneralTokenProvider();

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
        services.AddGeneralTokenProvider();
        services.TryAddSingleton(sp => new CopilotClient(new CopilotClientOptions
        {
            CliPath = ResolveCopilotCliPath(),
        }));
        services.TryAddSingleton<AzureDevOpsTools>();
        services.TryAddSingleton<StorageTools>();
        services.TryAddSingleton<CopilotAdapter>(sp =>
        {
            var client = sp.GetRequiredService<CopilotClient>();
            var configProvider = sp.GetRequiredService<IBuildDutyConfigProvider>();
            var storageProvider = sp.GetRequiredService<IStorageProvider>();

            var tools = new List<AIFunction>();
            tools.AddRange(sp.GetRequiredService<AzureDevOpsTools>().GetTools());
            tools.AddRange(sp.GetRequiredService<StorageTools>().GetTools());

            List<string>? skillsDirectories = new List<string> { Path.Combine(AppContext.BaseDirectory, "skills") };

            var gitHubToken = sp.GetRequiredService<IGeneralTokenProvider>()
                .GetTokenForRepositoryAsync("https://github.com").GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Failed to retrieve GitHub token. Ensure the GitHub CLI is authenticated via 'gh auth login'.");

            return new CopilotAdapter(client, configProvider, gitHubToken, tools, skillsDirectories);
        });
        return services;
    }

    private static IServiceCollection AddStorageProvider(this IServiceCollection services)
    {
        services.TryAddSingleton<IStorageProvider, StorageProvider>();
        return services;
    }

    private static IServiceCollection AddGeneralTokenProvider(this IServiceCollection services)
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

        // General token provider that routes to the correct backing provider by URL.
        services.TryAddSingleton<IGeneralTokenProvider>(sp =>
            new GeneralTokenProvider(
                sp.GetRequiredKeyedService<IRemoteTokenProvider>("azdo"),
                sp.GetRequiredKeyedService<IRemoteTokenProvider>("github")));

        return services;
    }

    private static string? ResolveCopilotCliPath()
    {
        var envPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        var exeName = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
        return Utilities.FindOnPath(exeName);
    }
}
