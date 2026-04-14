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
    private readonly IBuildDutyConfigProvider _configProvider;
    private readonly IStorageProvider _storageProvider;
    private readonly StorageTools _storageTools;
    private readonly AzureDevOpsTools _azureDevOpsTools;
    private readonly SemaphoreSlim _clientStartLock = new(1, 1);
    private CopilotClient? _client;
    private bool _clientStarted;

    public CopilotAdapter(
        IBuildDutyConfigProvider configProvider,
        IStorageProvider storageProvider,
        StorageTools storageTools,
        AzureDevOpsTools azureDevOpsTools)
    {
        _configProvider = configProvider;
        _storageProvider = storageProvider;
        _storageTools = storageTools;
        _azureDevOpsTools = azureDevOpsTools;
    }

    /// <summary>
    /// Run an AI action on a signal.
    /// </summary>
    public virtual async Task<string> RunSignalActionAsync(
        string signalId,
        string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalId);

        var client = await EnsureClientStartedAsync();

        var skills = new[]
        {
            "skills/analyze",
        };

        await using var session = await CopilotSessionFactory.CreateAsync(
            client,
            skills: skills,
            mcpServers: null,
            tools: BuildTools(),
            model: _configProvider.Get().Ai?.Model);

        var prompt = $"""
            Perform the following action on the given signal id:

            {action}

            SignalId: {signalId}
            """;
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt },
            timeout: TimeSpan.FromMinutes(10));

        return response?.Data?.Content ?? "(no response)";
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

        var client = await EnsureClientStartedAsync();

        var skills = new[]
        {
            "skills/reconcile-work-items",
        };

        await using var session = await CopilotSessionFactory.CreateAsync(
            client,
            skills: skills,
            mcpServers: null,
            tools: BuildTools(),
            model: _configProvider.Get().Ai?.Model);

        var prompt = $"""
            Perform the following action on the given set of signal ids:

            {action}

            SignalIds:
            {string.Join('\n', signalIds.Select(id => $"- {id}"))}
            """;
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt },
            timeout: TimeSpan.FromMinutes(10));

        return response?.Data?.Content ?? "(no response)";
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
        {
            await d.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    private async Task<CopilotClient> EnsureClientStartedAsync()
    {
        if (_clientStarted && _client is not null)
        {
            return _client;
        }

        await _clientStartLock.WaitAsync();
        try
        {
            if (_clientStarted)
            {
                return _client!;
            }

            _client ??= new CopilotClient(CreateCopilotClientOptions());
            await _client.StartAsync();
            _clientStarted = true;
            return _client;
        }
        finally
        {
            _clientStartLock.Release();
        }
    }

    private static CopilotClientOptions CreateCopilotClientOptions()
    {
        var options = new CopilotClientOptions();
        options.CliPath = ResolveCopilotCliPath();

        return options;
    }

    private static string ResolveCopilotCliPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        return "copilot";
    }

    private ICollection<AIFunction> BuildTools()
        => _storageTools.GetTools()
            .Concat(_azureDevOpsTools.GetTools())
            .ToList();

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
