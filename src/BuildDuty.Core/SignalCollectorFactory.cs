using BuildDuty.Core.Models;

namespace BuildDuty.Core;

public interface ISignalCollectorFactory
{
    ISignalCollector CreateCollector(object config);
}

public sealed class SignalCollectorFactory(
    Func<AzureDevOpsConfig, AzureDevOpsSignalCollector> azureDevOpsCollectorFactory,
    Func<GitHubConfig, GitHubSignalCollector> gitHubCollectorFactory) : ISignalCollectorFactory
{
    public ISignalCollector CreateCollector(object config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config switch
        {
            AzureDevOpsConfig azureDevOpsConfig => azureDevOpsCollectorFactory(azureDevOpsConfig),
            GitHubConfig gitHubConfig => gitHubCollectorFactory(gitHubConfig),
            _ => throw new NotSupportedException($"Unsupported collector config type: {config.GetType().Name}"),
        };
    }
}
