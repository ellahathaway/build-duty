namespace BuildDuty.Signals.Collection;

/// <summary>
/// Result of a signal collection run, including coverage tracking.
/// </summary>
public sealed record CollectionResult(
    IReadOnlyList<Signal> Signals,
    IReadOnlyList<CollectedScope> CoveredScopes,
    IReadOnlyList<CollectionFailure> Failures)
{
    public static CollectionResult Empty => new([], [], []);
}

/// <summary>
/// Represents a scope that was successfully scanned during collection.
/// </summary>
public sealed record CollectedScope(string ScopeKey);

/// <summary>
/// Represents a scope that failed to be scanned during collection.
/// </summary>
public sealed record CollectionFailure(string ScopeKey, string Reason);
