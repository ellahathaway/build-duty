namespace BuildDuty.Core;

/// <summary>
/// Stub GitHub signal service — produces placeholder work items for v1.
/// </summary>
public sealed class GitHubSignalService : ISignalService
{
    public string SourceName => "GitHub";

    public Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default)
    {
        // v1 stub: real implementation will query GitHub APIs
        IReadOnlyList<WorkItem> items = [];
        return Task.FromResult(items);
    }
}
