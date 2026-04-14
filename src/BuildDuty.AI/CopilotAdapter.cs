using BuildDuty.Core;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using System.Text;

namespace BuildDuty.AI;

/// <summary>
/// Executes AI analysis via the GitHub Copilot SDK. Uses
/// <see cref="CopilotSessionFactory"/> to create sessions pre-configured
/// with build-duty skills, data-access tools, and MCP servers.
/// </summary>
public class CopilotAdapter : IAsyncDisposable
{
    private static readonly object s_logWriteLock = new();

    private readonly IBuildDutyConfigProvider _configProvider;
    private readonly IStorageProvider _storageProvider;
    private readonly StorageTools _storageTools;
    private CopilotClient? _client;

    public CopilotAdapter(
        IBuildDutyConfigProvider configProvider,
        IStorageProvider storageProvider,
        StorageTools storageTools)
    {
        _configProvider = configProvider;
        _storageProvider = storageProvider;
        _storageTools = storageTools;
    }

    /// <summary>
    /// Run an AI action on a signal.
    /// </summary>
    public virtual async Task<string> RunSignalActionAsync(
        string signalId,
        string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);

        var logPath = CreateAgentLogFilePath("summarize", signalId);
        await using var logWriter = CreateLogWriter(logPath);
        await LogAsync(logWriter, $"start summarize signalId={signalId}");

        _client ??= new CopilotClient(new CopilotClientOptions());
        await _client.StartAsync();

        var skills = new[]
        {
            "skills/summarize",
        };

        await using var session = await CopilotSessionFactory.CreateAsync(
            _client,
            skills: skills,
            tools: _storageTools.GetTools(),
            model: _configProvider.Get().Ai?.Model);

        session.On(e =>
        {
            switch (e)
            {
                case ToolExecutionStartEvent toolStart:
                {
                    var toolName = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName ?? "?";
                    var server = toolStart.Data?.McpServerName;
                    var fqToolName = server is null ? toolName : $"{server}/{toolName}";
                    var argsText = toolStart.Data?.Arguments?.ToString();
                    var message = string.IsNullOrWhiteSpace(argsText)
                        ? $"tool-start: {fqToolName}"
                        : $"tool-start: {fqToolName}; args={argsText}";
                    LogSync(logWriter, message);
                    break;
                }

                case ToolExecutionCompleteEvent toolEnd:
                {
                    var success = toolEnd.Data?.Success == true;
                    LogSync(logWriter, success ? "tool-end: success=True" : "tool-end: success=False");
                    break;
                }

                case SessionErrorEvent sessionError:
                    LogSync(logWriter, $"session-error: {sessionError.Data}");
                    break;
            }
        });

        var prompt = $"""
            Perform the following action on the given signal id:

            {action}

            SignalId: {signalId}
            """;
        await LogAsync(logWriter, $"prompt: {prompt.Replace("\r", " ").Replace("\n", " ")}");

        try
        {
            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(5));

            var content = response?.Data?.Content ?? "(no response)";
            await LogAsync(logWriter, $"response: {content}");
            await LogAsync(logWriter, "completed summarize");
            return content;
        }
        catch (Exception ex)
        {
            await LogAsync(logWriter, $"error summarize: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Run an AI action across a set of signals.
    /// </summary>
    public virtual async Task<string> RunSignalSetActionAsync(
        IReadOnlyList<string> signalIds,
        string action)
    {
        if (signalIds.Count == 0)
        {
            return "(no signals)";
        }

        var logPath = CreateAgentLogFilePath("reconcile", string.Join("_", signalIds.Take(3)));
        await using var logWriter = CreateLogWriter(logPath);
        await LogAsync(logWriter, $"start reconcile signalCount={signalIds.Count}");

        _client ??= new CopilotClient(new CopilotClientOptions());
        await _client.StartAsync();

        var skills = new[]
        {
            "skills/reconcile-work-items",
        };

        await using var session = await CopilotSessionFactory.CreateAsync(
            _client,
            skills: skills,
            mcpServers: null,
            tools: _storageTools.GetTools(),
            model: _configProvider.Get().Ai?.Model);

        session.On(e =>
        {
            switch (e)
            {
                case ToolExecutionStartEvent toolStart:
                {
                    var toolName = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName ?? "?";
                    var server = toolStart.Data?.McpServerName;
                    var fqToolName = server is null ? toolName : $"{server}/{toolName}";
                    var argsText = toolStart.Data?.Arguments?.ToString();
                    var message = string.IsNullOrWhiteSpace(argsText)
                        ? $"tool-start: {fqToolName}"
                        : $"tool-start: {fqToolName}; args={argsText}";
                    LogSync(logWriter, message);
                    break;
                }

                case ToolExecutionCompleteEvent toolEnd:
                {
                    var success = toolEnd.Data?.Success == true;
                    LogSync(logWriter, success ? "tool-end: success=True" : "tool-end: success=False");
                    break;
                }

                case SessionErrorEvent sessionError:
                    LogSync(logWriter, $"session-error: {sessionError.Data}");
                    break;
            }
        });

        var prompt = $"""
            Perform the following action on the given set of signal ids:

            {action}

            SignalIds:
            {string.Join('\n', signalIds.Select(id => $"- {id}"))}
            """;
        await LogAsync(logWriter, $"prompt: {prompt.Replace("\r", " ").Replace("\n", " ")}");

        try
        {
            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(10));

            var content = response?.Data?.Content ?? "(no response)";
            await LogAsync(logWriter, $"response: {content}");
            await LogAsync(logWriter, "completed reconcile");
            return content;
        }
        catch (Exception ex)
        {
            await LogAsync(logWriter, $"error reconcile: {ex}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
        {
            await d.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    private string CreateAgentLogFilePath(string action, string key)
    {
        var configName = _configProvider.Get().Name;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".build-duty",
            configName,
            "agent-logs");
        Directory.CreateDirectory(root);

        var safeKey = string.Join("_", key.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeKey))
        {
            safeKey = "signal";
        }

        return Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{action}-{safeKey}.log");
    }

    private static StreamWriter CreateLogWriter(string logPath)
    {
        var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static Task LogAsync(StreamWriter writer, string message)
    {
        LogSync(writer, message);
        return Task.CompletedTask;
    }

    private static void LogSync(StreamWriter writer, string message)
    {
        lock (s_logWriteLock)
        {
            writer.WriteLine($"[{DateTime.UtcNow:O}] {message}");
            writer.Flush();
        }
    }

//     /// <summary>
//     /// Create a persistent review session for multi-turn interaction.
//     /// The caller is responsible for disposing the returned session.
//     /// </summary>
//     public async Task<ReviewSession> CreateReviewSessionAsync(
//         IReadOnlyList<string> skills,
//         Dictionary<string, object> mcpServers,
//         )
//     {
//         _client ??= new CopilotClient(_clientOptions);
//         await _client.StartAsync(ct);


//         var session = await CopilotSessionFactory.CreateAsync(
//             _client,
//             skills: skills,
//             mcpServers: mcpServers,
//             model: _configProvider.GetConfig().Ai?.Model,
//             tools: _tools,
//             ct: ct);

//         return new ReviewSession(session);
//     }
// }

// /// <summary>
// /// A long-lived session that supports multi-turn conversation with
// /// the AI agent. Created via <see cref="CopilotAdapter.CreateReviewSessionAsync"/>.
// /// Supports streaming events for live terminal rendering.
// /// </summary>
// public sealed class ReviewSession : IAsyncDisposable
// {
//     private readonly CopilotSession _session;
//     private volatile Action<AgentStreamEvent>? _streamHandler;

//     internal ReviewSession(CopilotSession session)
//     {
//         _session = session;
//         _session.On(e =>
//         {
//             var handler = _streamHandler;
//             if (handler is null) return;

//             switch (e)
//             {
//                 case AssistantMessageDeltaEvent delta:
//                     handler(new AgentStreamEvent { Type = "delta", Content = delta.Data?.DeltaContent });
//                     break;
//                 case ToolExecutionStartEvent toolStart:
//                     var name = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName ?? "?";
//                     var server = toolStart.Data?.McpServerName;
//                     handler(new AgentStreamEvent
//                     {
//                         Type = "tool-start",
//                         ToolName = server is not null ? $"{server}/{name}" : name,
//                         ToolArgs = toolStart.Data?.Arguments?.ToString(),
//                     });
//                     break;
//                 case ToolExecutionCompleteEvent toolEnd:
//                     handler(new AgentStreamEvent { Type = "tool-end", ToolSuccess = toolEnd.Data?.Success == true });
//                     break;
//                 case AssistantMessageEvent msg:
//                     handler(new AgentStreamEvent { Type = "message", Content = msg.Data?.Content });
//                     break;
//                 case SessionErrorEvent err:
//                     handler(new AgentStreamEvent { Type = "error", Content = err.Data?.ToString() });
//                     break;
//             }
//         });
//     }

//     /// <summary>Register a handler to receive streaming events (token deltas, tool calls, etc.).</summary>
//     public void OnStream(Action<AgentStreamEvent> handler) => _streamHandler = handler;

//     /// <summary>Send a message and wait for the agent's response.</summary>
//     public async Task<string> SendAsync(string prompt, )
//     {
//         var response = await _session.SendAndWaitAsync(
//             new MessageOptions { Prompt = prompt },
//             timeout: TimeSpan.FromMinutes(10),
//             cancellationToken: ct);

//         return response?.Data?.Content ?? "(no response)";
//     }

//     public async ValueTask DisposeAsync()
//     {
//         await _session.DisposeAsync();
//     }
}
