using BuildDuty.Core;

namespace BuildDuty.AI;

/// <summary>
/// Orchestrates AI job execution: resolves skills, manages state, invokes Copilot, persists results.
/// </summary>
public sealed class AiOrchestrator
{
    private readonly RouterManifest _router;
    private readonly CopilotAdapter _adapter;
    private readonly WorkItemStore _workItemStore;
    private readonly AiRunStore _aiRunStore;

    public AiOrchestrator(
        RouterManifest router,
        CopilotAdapter adapter,
        WorkItemStore workItemStore,
        AiRunStore aiRunStore)
    {
        _router = router;
        _adapter = adapter;
        _workItemStore = workItemStore;
        _aiRunStore = aiRunStore;
    }

    /// <summary>
    /// Run an AI job for a single work item.
    /// </summary>
    public async Task<AiRunResult> RunAsync(string workItemId, string job, CancellationToken ct = default)
    {
        var skill = _router.ResolveSkill(job);

        var workItem = await _workItemStore.LoadAsync(workItemId, ct)
            ?? throw new InvalidOperationException($"Work item '{workItemId}' not found.");

        // Transition to InProgress
        if (workItem.State == WorkItemState.Unresolved)
        {
            workItem.TransitionTo(WorkItemState.InProgress, $"AI job '{job}' started");
            await _workItemStore.SaveAsync(workItem, ct);
        }

        var runId = IdGenerator.NewAiRunId();
        var result = await _adapter.ExecuteAsync(workItem, job, skill, runId, ct);

        // Persist AI result
        await _aiRunStore.SaveAsync(result, ct);

        return result;
    }
}
