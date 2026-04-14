using System.Text.Json;
using BuildDuty.Core;
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
    private readonly IBuildDutyConfigProvider _configProvider;
    private readonly ICollection<AIFunction>? _tools;
    private readonly ICollection<string>? _skills;

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
        public const string Analyze = "analyze";
        public const string Reconcile = "reconcile";
        public const string Review = "review";
    }

    private static readonly List<CustomAgentConfig> CustomAgentConfigs =
    [
        new CustomAgentConfig
        {
            Name = Agents.Analyze,
            Description = "Analyzes a single signal — extracts cause, effect, and evidence.",
            Tools = ["get_signal", "read_pipeline_log", "create_signal_analysis", "update_signal_analysis", "remove_signal_analysis", "get_json_value"],
            Prompt = "You analyze a single signal to extract cause, effect, and evidence. Use the analyze-signal skill.",
        },
        new CustomAgentConfig
        {
            Name = Agents.Reconcile,
            Description = "Reconciles analyzed signals into work items — groups by root cause, creates/links/updates, resolves/reopens work items.",
            Tools = ["get_signal", "get_analysis_from_signal", "list_unresolved_work_items_with_signals", "list_orphaned_analyses", "list_unresolved_work_items_updated_in_triage", "create_work_item", "link_signal_to_work_item", "unlink_signal_from_work_item", "update_work_item", "resolve_work_item"],
            Prompt = "You reconcile analyzed signals into work items.",
        },
        new CustomAgentConfig
        {
            Name = Agents.Review,
            Description = "Interactive review of work items.",
            Tools = null, // all tools
            Prompt = "You help the user review work items interactively. Users will send unfiltered messages. You have access to MCP servers and other external tools beyond the built-in storage tools. When the user asks about live information (e.g. PR approval status, build results, issue comments), use available MCP tools or external integrations to fetch current data rather than relying solely on the signal snapshot.",
        },
    ];

    public CopilotAdapter(
        CopilotClient client,
        IBuildDutyConfigProvider configProvider,
        ICollection<AIFunction>? tools = null,
        ICollection<string>? skills = null)
    {
        _client = client;
        _configProvider = configProvider;
        _tools = tools;
        _skills = skills;
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
        var config = new SessionConfig
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SkillDirectories = _skills?.Select(Path.GetFullPath).ToList(),
            Streaming = streaming,
            SystemMessage = new SystemMessageConfig
            {
                Content = DefaultInstructions,
            },
            Tools = _tools,
            CustomAgents = CustomAgentConfigs,
            Agent = agent,
            Hooks = new SessionHooks
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
            }
        };

        var model = _configProvider.Get().Ai?.Model;
        if (model is not null)
        {
            config.Model = model;
        }

        var session = await _client.CreateSessionAsync(config);

        if (_skills != null)
        {
            foreach (var skill in _skills)
            {
                var skillName = Path.GetFileName(skill);
                await session.Rpc.Skills.EnableAsync(skillName);
            }
        }

        return session;
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
            }
        });
    }

    private static readonly JsonSerializerOptions s_logJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Subscribe to all SDK session events and write each as a JSONL line to <paramref name="writer"/> in real-time.
    /// </summary>
    public static IDisposable SubscribeToLog(CopilotSession session, StreamWriter writer)
    {
        var lockObj = new object();

        void Write(object entry)
        {
            var line = JsonSerializer.Serialize(entry, s_logJsonOptions);
            lock (lockObj)
            {
                writer.WriteLine(line);
            }
        }

        return session.On(e =>
        {
            var ts = DateTime.UtcNow.ToString("o");

            switch (e)
            {
                case AssistantReasoningDeltaEvent reasoning:
                    Write(new { ts, type = "reasoning-delta", content = reasoning.Data?.DeltaContent });
                    break;
                case AssistantReasoningEvent reasoning:
                    Write(new { ts, type = "reasoning", content = reasoning.Data?.Content });
                    break;
                case AssistantMessageDeltaEvent delta:
                    Write(new { ts, type = "message-delta", content = delta.Data?.DeltaContent });
                    break;
                case AssistantMessageEvent msg:
                    Write(new { ts, type = "message", content = msg.Data?.Content, phase = msg.Data?.Phase, outputTokens = msg.Data?.OutputTokens });
                    break;
                case AssistantUsageEvent usage:
                    Write(new { ts, type = "usage", model = usage.Data?.Model, inputTokens = usage.Data?.InputTokens, outputTokens = usage.Data?.OutputTokens, cacheReadTokens = usage.Data?.CacheReadTokens, cacheWriteTokens = usage.Data?.CacheWriteTokens, durationMs = usage.Data?.Duration, reasoningEffort = usage.Data?.ReasoningEffort });
                    break;
                case ToolExecutionStartEvent toolStart:
                    var name = toolStart.Data?.McpToolName ?? toolStart.Data?.ToolName;
                    var server = toolStart.Data?.McpServerName;
                    Write(new { ts, type = "tool-start", toolCallId = toolStart.Data?.ToolCallId, toolName = server is not null ? $"{server}/{name}" : name, args = toolStart.Data?.Arguments?.ToString() });
                    break;
                case ToolExecutionCompleteEvent toolEnd:
                    Write(new { ts, type = "tool-end", toolCallId = toolEnd.Data?.ToolCallId, success = toolEnd.Data?.Success, model = toolEnd.Data?.Model, result = toolEnd.Data?.Result?.ToString(), error = toolEnd.Data?.Error?.Message });
                    break;
                case SkillInvokedEvent skill:
                    Write(new { ts, type = "skill-invoked", name = skill.Data?.Name, path = skill.Data?.Path });
                    break;
                case SessionErrorEvent err:
                    Write(new { ts, type = "error", errorType = err.Data?.ErrorType, message = err.Data?.Message, statusCode = err.Data?.StatusCode });
                    break;
                case HookStartEvent hook:
                    Write(new { ts, type = "hook-start", hookType = hook.Data?.HookType, hookInvocationId = hook.Data?.HookInvocationId });
                    break;
                case HookEndEvent hook:
                    Write(new { ts, type = "hook-end", hookType = hook.Data?.HookType, hookInvocationId = hook.Data?.HookInvocationId, success = hook.Data?.Success });
                    break;
            }
        });
    }
}
