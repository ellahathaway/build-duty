using BuildDuty.Core;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Executes AI analysis via the GitHub Copilot SDK. Uses
/// <see cref="CopilotSessionFactory"/> to create sessions pre-configured
/// with build-duty skills, data-access tools, and MCP servers.
/// </summary>
public class CopilotAdapter : IAsyncDisposable
{
    private readonly CopilotClientOptions _clientOptions;
    private readonly IReadOnlyList<AIFunction> _tools;
    private readonly string? _model;
    private CopilotClient? _client;

    public CopilotAdapter(
        CopilotClientOptions clientOptions,
        IReadOnlyList<AIFunction> tools,
        string? model = null)
    {
        _clientOptions = clientOptions;
        _tools = tools;
        _model = model;
    }

    /// <summary>
    /// Execute an AI action against a work item, optionally including prior run context.
    /// </summary>
    public virtual async Task<AiRunResult> ExecuteAsync(
        WorkItem workItem,
        string action,
        string runId,
        AiRunResult? priorRun = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        try
        {
            _client ??= new CopilotClient(_clientOptions);
            await _client.StartAsync(ct);

            await using var session = await CopilotSessionFactory.CreateAsync(
                _client,
                model: _model,
                tools: _tools,
                ct: ct);

            var priorContext = "";
            if (priorRun is not null)
            {
                priorContext = $"""

                    IMPORTANT: This work item was previously analyzed (run {priorRun.RunId} at {priorRun.FinishedUtc:u}).
                    The prior action was: {priorRun.Job}
                    The prior result was:
                    {priorRun.Summary}

                    Do NOT repeat the same analysis. Build on the prior result — add new findings, update status, or note changes since the last run.
                    """;
            }

            var prompt = $"""
                Use the available tools to look up work item "{workItem.Id}" ({workItem.Title}) and its signals.
                Then perform the following action:

                {action}
                {priorContext}
                """;

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(15),
                cancellationToken: ct);

            var content = response?.Data?.Content ?? "(no response)";

            return new AiRunResult
            {
                RunId = runId,
                WorkItemId = workItem.Id,
                Job = action,
                Skill = "(auto)",
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
                ExitCode = 0,
                Summary = content,
                Stdout = content,
                Stderr = null
            };
        }
        catch (Exception ex)
        {
            return new AiRunResult
            {
                RunId = runId,
                WorkItemId = workItem.Id,
                Job = action,
                Skill = "(auto)",
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
                ExitCode = 1,
                Summary = $"Error: {ex.Message}",
                Stdout = null,
                Stderr = ex.ToString()
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
            await d.DisposeAsync();
        GC.SuppressFinalize(this);
    }


}
