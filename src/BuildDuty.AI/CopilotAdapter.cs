using BuildDuty.Core;

namespace BuildDuty.AI;

/// <summary>
/// Adapter for invoking Copilot CLI with a bundled skill.
/// v1 stub: logs the invocation and returns a placeholder result.
/// Real implementation will shell out to `copilot-cli`.
/// </summary>
public class CopilotAdapter
{
    /// <summary>
    /// Execute an AI job against a work item using the specified skill.
    /// </summary>
    public virtual Task<AiRunResult> ExecuteAsync(
        WorkItem workItem,
        string job,
        string skill,
        string runId,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        // v1 stub: simulate Copilot CLI invocation
        var result = new AiRunResult
        {
            RunId = runId,
            WorkItemId = workItem.Id,
            Job = job,
            Skill = skill,
            StartedUtc = started,
            FinishedUtc = DateTime.UtcNow,
            ExitCode = 0,
            Summary = $"[stub] {skill} analysis for {workItem.Id}: {workItem.Title}",
            Stdout = $"Copilot CLI stub — skill={skill}, workitem={workItem.Id}",
            Stderr = null
        };

        return Task.FromResult(result);
    }
}
