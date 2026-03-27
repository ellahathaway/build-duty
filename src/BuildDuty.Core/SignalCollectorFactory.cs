using BuildDuty.Core.Models;

namespace BuildDuty.Core;

public interface ISignalCollectorFactory
{
    ISignalCollector CreateCollector<TConfig>() where TConfig : class;
}

public sealed class SignalCollectorFactory(IServiceProvider serviceProvider) : ISignalCollectorFactory
{
    public ISignalCollector CreateCollector<TConfig>() where TConfig : class
    {
        return typeof(TConfig) switch
        {
            var t when t == typeof(AzureDevOpsConfig) =>
                (ISignalCollector)serviceProvider.GetService(typeof(AzureDevOpsSignalCollector))!
                    ?? throw new InvalidOperationException("AzureDevOpsSignalCollector is not registered."),
            var t when t == typeof(GitHubConfig) =>
                (ISignalCollector)serviceProvider.GetService(typeof(GitHubSignalCollector))!
                    ?? throw new InvalidOperationException("GitHubSignalCollector is not registered."),
            _ => throw new NotSupportedException($"Unsupported collector config type: {typeof(TConfig).Name}"),
        };
    }
}
