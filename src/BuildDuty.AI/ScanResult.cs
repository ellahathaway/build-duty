namespace BuildDuty.AI;

/// <summary>
/// Result of a single AI scan agent invocation.
/// </summary>
public sealed class ScanResult
{
    public required string Source { get; init; }
    public required bool Success { get; init; }
    public required string Summary { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public string? Error { get; init; }

    public TimeSpan Duration => FinishedUtc - StartedUtc;
}
