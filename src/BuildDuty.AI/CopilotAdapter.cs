using BuildDuty.Core;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.AI;

/// <summary>
/// Executes AI analysis via the GitHub Copilot SDK. Uses
/// <see cref="CopilotSessionFactory"/> to create sessions pre-configured
/// with build-duty skills, data-access tools, and MCP servers.
/// </summary>
public class CopilotAdapter : IAsyncDisposable
{
    private readonly IBuildDutyConfigProvider _configProvider;
    private CopilotClient? _client;

    public CopilotAdapter(IBuildDutyConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    /// <summary>
    /// Run an AI action on a signal.
    /// </summary>
    public virtual async Task<SignalResult> RunSignalActionAsync(
        ISignal signal,
        string action,
        CancellationToken ct = default)
    {
        try
        {
            _client ??= new CopilotClient(new CopilotClientOptions());
            await _client.StartAsync(ct);

            var mcpServers = signal switch
            {
                AzureDevOpsPipelineSignal pipelineSignal => GetAzureDevOpsPipelineServer(pipelineSignal),
                _ => new Dictionary<string, object>(),
            };

            var skills = new[]
            {
                "skills/summarize",
                "skills/cluster",
            };

            await using var session = await CopilotSessionFactory.CreateAsync(
                _client,
                skills: skills,
                mcpServers: mcpServers,
                model: _configProvider.GetConfig().Ai?.Model,
                ct: ct);

            var signalPayload = JsonSerializer.Serialize(signal, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });

            var prompt = $"""
                Perform the following action on the given signal:

                {action}

                Signal payload (JSON):
                {signalPayload}
                """;

            var response = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                timeout: TimeSpan.FromMinutes(5),
                cancellationToken: ct);

            var content = response?.Data?.Content ?? "(no response)";

            return new SignalResult
            {
                Signal = signal,
                Action = action,
                Success = true,
                Response = content,
            };
        }
        catch (Exception ex)
        {
            return new SignalResult
            {
                Signal = signal,
                Action = action,
                Success = false,
                Response = $"Error: {ex.Message}",
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
            await d.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, object> GetAzureDevOpsPipelineServer(AzureDevOpsPipelineSignal signal)
    {
        string org = signal.Info.Build.Url.Split('/')[3];
        return new Dictionary<string, object>
        {
            ["azure-devops"] = new McpLocalServerConfig
            {
                Command = "npx",
                Args = ["-y", "@azure-devops/mcp", org, "-a", "azcli", "-d", "pipelines"],
                Tools = ["*"],
                Env = new Dictionary<string, string>
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                },
            }
        };
    }

//     /// <summary>
//     /// Create a persistent review session for multi-turn interaction.
//     /// The caller is responsible for disposing the returned session.
//     /// </summary>
//     public async Task<ReviewSession> CreateReviewSessionAsync(
//         IReadOnlyList<string> skills,
//         Dictionary<string, object> mcpServers,
//         CancellationToken ct = default)
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
//     public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
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
