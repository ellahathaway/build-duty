using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using Maestro.Common;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Signals.Collection;

public interface ISignalProvider
{
    Task<CollectionResult> CollectSignalsAsync(BuildDutyConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// Collects all build-duty signals from a <see cref="BuildDutyConfig"/>.
/// </summary>
public sealed class SignalProvider : ISignalProvider
{
    private readonly IRemoteTokenProvider _tokenProvider;
    private readonly ReleaseBranchResolver _branchResolver;
    private readonly ILogger _logger;

    public SignalProvider(IRemoteTokenProvider tokenProvider, ILogger logger, ReleaseBranchResolver branchResolver)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
        _branchResolver = branchResolver;
    }

    public async Task<CollectionResult> CollectSignalsAsync(BuildDutyConfig config, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collecting signals for configuration '{Config}'", config.Name);

        var collectors = CreateCollectors(config);

        var signalTasks = collectors.Select(collector => collector.CollectAsync(cancellationToken));
        var collectorResults = await Task.WhenAll(signalTasks);

        var signals = new List<Signal>();
        var scopes = new List<CollectedScope>();
        var failures = new List<CollectionFailure>();

        foreach (var result in collectorResults)
        {
            signals.AddRange(result.Signals);
            scopes.AddRange(result.CoveredScopes);
            failures.AddRange(result.Failures);
        }

        _logger.LogInformation("Collected {Count} signals for configuration '{Config}'", signals.Count, config.Name);

        return new CollectionResult(signals, scopes, failures);
    }

    /// <summary>
    /// Creates all applicable signal collectors for the given configuration.
    /// </summary>
    private IReadOnlyList<ISignalCollector> CreateCollectors(BuildDutyConfig config)
    {
        var collectors = new List<ISignalCollector>();

        if (config.AzureDevOps is not null)
        {
            collectors.Add(new AzureDevOpsSignalCollector(config.AzureDevOps, _tokenProvider, _logger, _branchResolver));
        }

        if (config.GitHub is not null)
        {
            collectors.Add(new GitHubSignalCollector(config.GitHub, _tokenProvider, _logger));
        }

        return collectors;
    }
}
