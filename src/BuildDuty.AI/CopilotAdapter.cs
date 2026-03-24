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
    /// Run AI triage against a specific work item, optionally including prior run context.
    /// </summary>
    public virtual async Task<TriageResult> TriageAsync(
        WorkItem workItem,
        string action,
        string runId,
        IReadOnlyList<string> skills,
        Dictionary<string, object> mcpServers,
        TriageResult? priorRun = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        try
        {
            _client ??= new CopilotClient(_clientOptions);
            await _client.StartAsync(ct);

            await using var session = await CopilotSessionFactory.CreateAsync(
                _client,
                skills: skills,
                mcpServers: mcpServers,
                model: _model,
                tools: _tools,
                ct: ct);

            var priorContext = "";
            if (priorRun is not null)
            {
                priorContext = $"""

                    IMPORTANT: This work item was previously analyzed (run {priorRun.RunId} at {priorRun.FinishedUtc:u}).
                    The prior action was: {priorRun.Action}
                    The prior result was:
                    {priorRun.Summary}

                    Do NOT repeat the same analysis. Build on the prior result — add new findings, update status, or note changes since the last run.
                    """;
            }

            var prompt = $"""
                Use the available tools to look up work item "{workItem.Id}" ({workItem.Title}) and its sources.
                Then perform the following action:

                {action}
                {priorContext}
                """;

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: ct);

            var content = response?.Data?.Content ?? "(no response)";

            return new TriageResult
            {
                RunId = runId,
                WorkItemId = workItem.Id,
                Action = action,
                Success = true,
                Summary = content,
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            return new TriageResult
            {
                RunId = runId,
                WorkItemId = workItem.Id,
                Action = action,
                Success = false,
                Summary = $"Error: {ex.Message}",
                Error = ex.ToString(),
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
            };
        }
    }

    /// <summary>
    /// Run a scan agent — a free-form prompt for source-specific work item collection.
    /// </summary>
    public virtual async Task<ScanResult> ScanSourceAsync(
        string prompt,
        string sourceName,
        IReadOnlyList<string> skills,
        Dictionary<string, object> mcpServers,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        try
        {
            _client ??= new CopilotClient(_clientOptions);
            await _client.StartAsync(ct);

            await using var session = await CopilotSessionFactory.CreateAsync(
                _client,
                skills: skills,
                mcpServers: mcpServers,
                model: _model,
                tools: _tools,
                ct: ct);

            // Temporary: stream full agent output to log file for debugging
            var logFile = Path.Combine(Path.GetTempPath(), $"build-duty-{sourceName.Replace(" ", "-").ToLowerInvariant()}.log");
            var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };
            logWriter.WriteLine($"=== {sourceName} scan started at {DateTime.UtcNow:O} ===\n");
            Console.Error.WriteLine($"[DIAG] Agent log: {logFile}");

            session.On(e =>
            {
                var ts = $"[{DateTime.UtcNow:HH:mm:ss}]";
                switch (e)
                {
                    case AssistantMessageDeltaEvent delta:
                        logWriter.Write(delta.Data?.DeltaContent ?? "");
                        break;
                    case AssistantMessageEvent msg:
                        logWriter.WriteLine($"\n\n--- Full message ---\n{msg.Data?.Content}\n---\n");
                        break;
                    case ToolExecutionStartEvent toolStart:
                        var name = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName ?? "?";
                        var server = toolStart.Data?.McpServerName;
                        var label = server is not null ? $"{server}/{name}" : name;
                        logWriter.WriteLine($"\n{ts} TOOL CALL: {label}");
                        logWriter.WriteLine($"  args: {toolStart.Data?.Arguments}");
                        break;
                    case ToolExecutionCompleteEvent toolEnd:
                        var ok = toolEnd.Data?.Success == true ? "✓" : "✗";
                        logWriter.WriteLine($"{ts} TOOL RESULT: {ok}");
                        break;
                    case SessionErrorEvent err:
                        logWriter.WriteLine($"\n{ts} ERROR: {err.Data}");
                        break;
                }
            });

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(10),
                cancellationToken: ct);

            var content = response?.Data?.Content ?? "(no response)";

            return new ScanResult
            {
                Source = sourceName,
                Success = true,
                Summary = content,
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            return new ScanResult
            {
                Source = sourceName,
                Success = false,
                Summary = $"Error: {ex.Message}",
                Error = ex.ToString(),
                StartedUtc = started,
                FinishedUtc = DateTime.UtcNow,
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
            await d.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Create a persistent review session for multi-turn interaction.
    /// The caller is responsible for disposing the returned session.
    /// </summary>
    public async Task<ReviewSession> CreateReviewSessionAsync(
        IReadOnlyList<string> skills,
        Dictionary<string, object> mcpServers,
        CancellationToken ct = default)
    {
        _client ??= new CopilotClient(_clientOptions);
        await _client.StartAsync(ct);

        var session = await CopilotSessionFactory.CreateAsync(
            _client,
            skills: skills,
            mcpServers: mcpServers,
            model: _model,
            tools: _tools,
            ct: ct);

        return new ReviewSession(session);
    }
}

/// <summary>
/// A long-lived session that supports multi-turn conversation with
/// the AI agent. Created via <see cref="CopilotAdapter.CreateReviewSessionAsync"/>.
/// Supports streaming events for live terminal rendering.
/// </summary>
public sealed class ReviewSession : IAsyncDisposable
{
    private readonly CopilotSession _session;
    private volatile Action<AgentStreamEvent>? _streamHandler;

    internal ReviewSession(CopilotSession session)
    {
        _session = session;
        _session.On(e =>
        {
            var handler = _streamHandler;
            if (handler is null) return;

            switch (e)
            {
                case AssistantMessageDeltaEvent delta:
                    handler(new AgentStreamEvent { Type = "delta", Content = delta.Data?.DeltaContent });
                    break;
                case ToolExecutionStartEvent toolStart:
                    var name = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName ?? "?";
                    var server = toolStart.Data?.McpServerName;
                    handler(new AgentStreamEvent
                    {
                        Type = "tool-start",
                        ToolName = server is not null ? $"{server}/{name}" : name,
                        ToolArgs = toolStart.Data?.Arguments?.ToString(),
                    });
                    break;
                case ToolExecutionCompleteEvent toolEnd:
                    handler(new AgentStreamEvent { Type = "tool-end", ToolSuccess = toolEnd.Data?.Success == true });
                    break;
                case AssistantMessageEvent msg:
                    handler(new AgentStreamEvent { Type = "message", Content = msg.Data?.Content });
                    break;
                case SessionErrorEvent err:
                    handler(new AgentStreamEvent { Type = "error", Content = err.Data?.ToString() });
                    break;
            }
        });
    }

    /// <summary>Register a handler to receive streaming events (token deltas, tool calls, etc.).</summary>
    public void OnStream(Action<AgentStreamEvent> handler) => _streamHandler = handler;

    /// <summary>Send a message and wait for the agent's response.</summary>
    public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        var response = await _session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt },
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: ct);

        return response?.Data?.Content ?? "(no response)";
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
    }
}
