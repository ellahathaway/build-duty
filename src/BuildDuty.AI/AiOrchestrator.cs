using BuildDuty.Core;

namespace BuildDuty.AI;

/// <summary>
/// Orchestrates AI execution: manages work item state, invokes the adapter, persists results.
/// </summary>
public sealed class AiOrchestrator
{
    private readonly CopilotAdapter _adapter;
    private readonly WorkItemStore _workItemStore;
    private readonly AiRunStore _aiRunStore;

    public AiOrchestrator(
        CopilotAdapter adapter,
        WorkItemStore workItemStore,
        AiRunStore aiRunStore)
    {
        _adapter = adapter;
        _workItemStore = workItemStore;
        _aiRunStore = aiRunStore;
    }

    /// <summary>
    /// Run a free-form AI action for a single work item.
    /// </summary>
    public async Task<AiRunResult> RunAsync(string workItemId, string action, CancellationToken ct = default)
    {
        var workItem = await _workItemStore.LoadAsync(workItemId, ct)
            ?? throw new InvalidOperationException($"Work item '{workItemId}' not found.");

        // Look up prior run so the AI can build on previous analysis
        AiRunResult? priorRun = null;
        if (workItem.State == WorkItemState.InProgress)
        {
            priorRun = await _aiRunStore.FindLatestForWorkItemAsync(workItemId, ct);
        }

        // Transition to InProgress
        if (workItem.State == WorkItemState.Unresolved)
        {
            workItem.TransitionTo(WorkItemState.InProgress, $"AI action: {action}");
            await _workItemStore.SaveAsync(workItem, ct);
        }

        var runId = IdGenerator.NewAiRunId();
        var result = await _adapter.ExecuteAsync(workItem, action, runId, priorRun, ct);

        // Persist AI result
        await _aiRunStore.SaveAsync(result, ct);

        return result;
    }
}
