namespace BuildDuty.Core;

/// <summary>
/// Contract for signal collection services (Azure DevOps, GitHub, etc.).
/// </summary>
public interface ISignalService
{
    string SourceName { get; }
    Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default);
}
