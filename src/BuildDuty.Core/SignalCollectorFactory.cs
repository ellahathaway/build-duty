using BuildDuty.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BuildDuty.Core;

public interface ISignalCollectorFactory
{
    ISignalCollector? CreateCollector<TConfig>() where TConfig : class;
}

public sealed class SignalCollectorFactory(
    IServiceProvider serviceProvider,
    IBuildDutyConfigProvider configProvider) : ISignalCollectorFactory
{
    public ISignalCollector? CreateCollector<TConfig>() where TConfig : class
    {
        var config = configProvider.Get();

        return typeof(TConfig) switch
        {
            var t when t == typeof(AzureDevOpsConfig) =>
                config.AzureDevOps is null
                    ? null
                    : ActivatorUtilities.CreateInstance<AzureDevOpsSignalCollector>(serviceProvider, config.AzureDevOps),
            var t when t == typeof(GitHubConfig) =>
                config.GitHub is null
                    ? null
                    : ActivatorUtilities.CreateInstance<GitHubSignalCollector>(serviceProvider, config.GitHub),
            _ => throw new NotSupportedException($"Unsupported collector config type: {typeof(TConfig).Name}"),
        };
    }
}
