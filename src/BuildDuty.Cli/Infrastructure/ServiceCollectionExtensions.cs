using BuildDuty.Core;
using BuildDuty.AI;
using BuildDuty.AI.Tools;
using GitHub.Copilot.SDK;
using Maestro.Common;
using Microsoft.Extensions.AI;
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
        services.AddRemoteTokenProvider();
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

            var skillsDirectory = Path.Combine(AppContext.BaseDirectory, "skills");
            var skills = Directory.Exists(skillsDirectory)
                ? Directory.GetDirectories(skillsDirectory).ToList()
                : null;

            var gitHubToken = sp.GetRequiredService<IRemoteTokenProvider>()
                .GetTokenForRepositoryAsync("https://github.com").GetAwaiter().GetResult() ?? string.Empty;

            return new CopilotAdapter(client, configProvider, gitHubToken, tools, skills);
        });
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

        // Default provider used by components that do not request a keyed instance.
        services.TryAddSingleton<IRemoteTokenProvider>(sp =>
            new RoutedRemoteTokenProvider(
                sp.GetRequiredKeyedService<IRemoteTokenProvider>("azdo"),
                sp.GetRequiredKeyedService<IRemoteTokenProvider>("github")));

        return services;
    }

    private sealed class RoutedRemoteTokenProvider(
        IRemoteTokenProvider azureDevOpsTokenProvider,
        IRemoteTokenProvider githubTokenProvider) : IRemoteTokenProvider
    {
        public string GetTokenForRepository(string repoUri)
            => GetProvider(repoUri).GetTokenForRepository(repoUri)
                ?? throw new InvalidOperationException($"No token available for repository '{repoUri}'.");

        public Task<string?> GetTokenForRepositoryAsync(string repoUri)
            => GetProvider(repoUri).GetTokenForRepositoryAsync(repoUri);

        private IRemoteTokenProvider GetProvider(string repoUri)
        {
            if (!Uri.TryCreate(repoUri, UriKind.Absolute, out var uri))
            {
                return githubTokenProvider;
            }

            if (uri.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
            {
                return azureDevOpsTokenProvider;
            }

            if (uri.Host.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return githubTokenProvider;
            }

            return githubTokenProvider;
        }
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
