using BuildDuty.Signals.Configuration;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Signals.Collection;

public interface ISignalProvider
{
    Task<List<Signal>> CollectSignalsAsync(BuildDutyConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// Collects all build-duty signals from a <see cref="BuildDutyConfig"/>.
/// </summary>
public sealed class SignalProvider : ISignalProvider
{
    private readonly ITokenProvider _tokenProvider;
    private readonly ReleaseBranchResolver _branchResolver;
    private readonly ILogger _logger;

    public SignalProvider(ITokenProvider tokenProvider, ILogger logger, ReleaseBranchResolver? branchResolver = null)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
        _branchResolver = branchResolver ?? new ReleaseBranchResolver();
    }

    public async Task<List<Signal>> CollectSignalsAsync(BuildDutyConfig config, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collecting signals for configuration '{Config}'", config.Name);

        var collectors = CreateCollectors(config);
        var signals = new List<Signal>();

        var signalTasks = collectors.Select(collector => collector.CollectAsync(cancellationToken));
        var signalResults = await Task.WhenAll(signalTasks);

        foreach (var result in signalResults)
        {
            signals.AddRange(result);
        }

        _logger.LogInformation("Collected {Count} signals for configuration '{Config}'", signals.Count, config.Name);

        return signals;
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
