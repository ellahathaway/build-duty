using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Executes AI analysis via the GitHub Copilot SDK. Uses a single
/// <see cref="CopilotClient"/> and creates sessions on demand.
/// Multiple sessions can run concurrently from the same client.
/// </summary>
public class CopilotAdapter
{
    private readonly CopilotClient _client;
    private readonly List<AIFunction> _tools;
    private readonly List<string>? _skillsDirectories;
    private readonly string _gitHubToken;
    private readonly string? _model;

    private const string DefaultInstructions = """
        You are a build-duty agent that helps engineers with the build-duty process (non-interactive triaging and interactive reviewing).

        You work with **triage runs** (a single triage process for a set of related signals and work items, identified by a triage ID),
        **signals** (pre-collected snapshots of Azure DevOps pipeline runs, GitHub issues/PRs)
        and **work items** (groups of correlated signals by root cause).

        ### Triage Runs
        A triage run represents a single execution of the triage process, identified by a triage ID.
        The triage run tracks the status of the triage process (e.g. collecting signals, analyzing, recommending actions, etc.) and the associated signals and work items.

        ### Signals
        Each signal represents a single CI/CD instance (e.g. a pipeline run, a GitHub issue, or a GitHub pull request) collected during a triage run.
        The content of the signal should be treated as factual evidence at the time of triage, indicated by the corresponding triage ID.
        Signals may be updated with new evidence over time.

        ### Work Items
        A work item represents a cluster of signals that are believed to be caused by the same underlying issue.
        The work item may be updated over time with new signals and evidence, and eventually resolved.
        """;

    public static class Agents
    {
        public const string PipelineLog = "pipeline_log";
        public const string Signal = "signals";
        public const string WorkItem = "work_item";
        public const string Review = "review";
    }

    private static readonly List<CustomAgentConfig> CustomAgentConfigs =
    [
        new CustomAgentConfig
        {
            Name = Agents.PipelineLog,
            Description = "Reads and analyzes Azure DevOps build pipeline logs using the read_pipeline_log tool. Use this agent when you need to retrieve and interpret CI/CD log content.",
            Tools = [ "read_pipeline_log", "get_timeline_records" ],
            Prompt = "You read and analyze Azure DevOps pipeline logs to extract relevant information. Use read_pipeline_log to retrieve log content by signal ID and log ID. Use get_timeline_records to list timeline records for a signal.",
        },
        new CustomAgentConfig
        {
            Name = Agents.Signal,
            Description = "Works with signals from a build-duty triage run. Analyzes signals and creates signal analyses.",
            Tools = [ "get_signal", "create_signal_analysis", "update_signal_analysis", "resolve_signal_analysis", "get_json_value", "get_signal_analyses", "list_analyses_for_triage", "get_analysis", "list_work_items_for_analysis" ],
            Prompt = "You can list signals, read signal/analysis details, edit signals/analyses, create/update/resolve signal analyses, and extract JSON values from signals.",
        },
        new CustomAgentConfig
        {
            Name = Agents.WorkItem,
            Description = "Works with work items from a build-duty triage run.",
            Tools = [ "list_work_items", "list_work_items_for_triage", "get_work_item", "create_work_item", "update_work_item_metadata", "resolve_work_item", "link_analysis_to_work_item", "unlink_analysis_from_work_item", "get_json_value" ],
            Prompt = "You can list work items, read work item details, edit/update work items, adjust linked signals/analyses for work items, and extract JSON values from work items.",
        }
    ];

    public CopilotAdapter(
        CopilotClient client,
        string gitHubToken,
        ICollection<AIFunction>? tools = null,
        List<string>? skillsDirectories = null,
        string model = "")
    {
        _client = client;
        _tools = tools is not null ? new List<AIFunction>(tools) : [];
        _skillsDirectories = skillsDirectories;
        _gitHubToken = gitHubToken;
        _model = model;
    }

    /// <summary>
    /// Run an AI prompt in a copilot session
    /// </summary>
    public virtual async Task<AssistantMessageEvent?> RunPromptAsync(CopilotSession session, string prompt, CancellationToken cancellationToken = default)
    {
        return await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt },
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Create a persistent session.
    /// </summary>
    public virtual async Task<CopilotSession> CreateSessionAsync(
        bool streaming = false,
        string? agent = null,
        bool throwAfterRetries = false)
    {
        var hooks = new SessionHooks
        {
            OnErrorOccurred = async (input, _) =>
            {
                if (input.Recoverable == true)
                {
                    return new ErrorOccurredHookOutput
                    {
                        ErrorHandling = "retry",
                        RetryCount = 2,
                    };
                }

                if (throwAfterRetries)
                {
                    throw new InvalidOperationException(
                        $"Copilot session error [{input.ErrorContext}]: {input.Error}");
                }

                return new ErrorOccurredHookOutput
                {
                    ErrorHandling = "skip",
                    UserNotification = $"An error occurred, skipping: {input.Error}",
                };
            }
        };

        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SkillDirectories = _skillsDirectories,
            WorkingDirectory = AppContext.BaseDirectory,
            Streaming = streaming,
            SystemMessage = new SystemMessageConfig
            {
                Content = DefaultInstructions,
            },
            Tools = _tools,
            CustomAgents = CustomAgentConfigs,
            Agent = agent,
            Hooks = hooks,
            McpServers = await BuildGitHubMcpServerAsync(),
        };

        if (_model is not null)
        {
            config.Model = _model;
        }

        var session = await _client.CreateSessionAsync(config);

        return session;
    }

    private async Task<Dictionary<string, object>> BuildGitHubMcpServerAsync()
    {
        if (string.IsNullOrWhiteSpace(_gitHubToken))
        {
            throw new InvalidOperationException("GitHub token is required for the GitHub MCP server but was not found.");
        }

        return new Dictionary<string, object>
        {
            ["github"] = new McpRemoteServerConfig
            {
                Url = "https://api.githubcopilot.com/mcp/",
                Tools = ["*"],
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {_gitHubToken}"
                }
            }
        };
    }

    /// <summary>
    /// Permanently deletes a session and removes it from the local session list.
    /// Unlike <see cref="CopilotSession.DisposeAsync"/>, which only releases in-memory resources,
    /// this method removes all session data from disk so the session no longer appears in the CLI session list.
    /// </summary>
    public virtual async Task DeleteSessionAsync(CopilotSession session)
    {
        await _client.DeleteSessionAsync(session.SessionId);
    }

    /// <summary>
    /// Create a session, run a prompt, then clean up. Wraps the common
    /// create → run → delete pattern into a single call.
    /// </summary>
    public virtual async Task RunSessionAsync(
        string prompt,
        string? agent = null,
        bool streaming = false,
        bool throwAfterRetries = false)
    {
        await using var session = await CreateSessionAsync(
            streaming: streaming,
            agent: agent,
            throwAfterRetries: throwAfterRetries);

        try
        {
            await RunPromptAsync(session, prompt);
        }
        finally
        {
            await DeleteSessionAsync(session);
        }
    }

    /// <summary>
    /// Subscribe to SDK session events and translate them to <see cref="AgentStreamEvent"/>.
    /// </summary>
    public static IDisposable SubscribeToStream(CopilotSession session, Action<AgentStreamEvent> handler)
    {
        return session.On(e =>
        {
            switch (e)
            {
                case AssistantReasoningDeltaEvent reasoning:
                    handler(new AgentStreamEvent { Type = "reasoning", Content = reasoning.Data?.DeltaContent });
                    break;
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
                case SessionErrorEvent err:
                    handler(new AgentStreamEvent { Type = "error", Content = err.Data?.ToString() });
                    break;
                case SessionMcpServersLoadedEvent mcpLoaded:
                    var serverNames = mcpLoaded.Data?.Servers?.Select(s => $"{s.Name}({s.Status})") ?? [];
                    handler(new AgentStreamEvent { Type = "mcp-loaded", Content = string.Join(", ", serverNames) });
                    break;
                case SessionMcpServerStatusChangedEvent mcpStatus:
                    handler(new AgentStreamEvent { Type = "mcp-status", Content = $"{mcpStatus.Data?.ServerName}: {mcpStatus.Data?.Status}" });
                    break;
            }
        });
    }
}
