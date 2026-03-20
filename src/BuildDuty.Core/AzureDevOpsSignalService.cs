namespace BuildDuty.Core;

/// <summary>
/// Stub Azure DevOps signal service — produces placeholder work items for v1.
/// </summary>
public sealed class AzureDevOpsSignalService : ISignalService
{
    public string SourceName => "AzureDevOps";

    public Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default)
    {
        // v1 stub: real implementation will query ADO REST APIs
        IReadOnlyList<WorkItem> items = [];
        return Task.FromResult(items);
    }
}
