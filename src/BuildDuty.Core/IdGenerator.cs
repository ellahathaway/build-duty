namespace BuildDuty.Core;

/// <summary>
/// Generates deterministic work item IDs with a wi_ prefix.
/// </summary>
public static class IdGenerator
{
    public static string NewWorkItemId() => $"wi_{Guid.NewGuid():N}";
    public static string NewCorrelationId() => $"corr_{Guid.NewGuid():N}";
    public static string NewTriageRunId() => $"triage_{Guid.NewGuid():N}";
}
