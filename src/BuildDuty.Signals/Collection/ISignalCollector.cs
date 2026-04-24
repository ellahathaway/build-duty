namespace BuildDuty.Signals.Collection;

/// <summary>
/// Collects signals from an external source (GitHub, Azure DevOps, etc.).
/// </summary>
public interface ISignalCollector
{
    /// <summary>
    /// Collects signals from the configured source and returns them in-memory.
    /// </summary>
    Task<IReadOnlyList<Signal>> CollectAsync(CancellationToken cancellationToken = default);
}
