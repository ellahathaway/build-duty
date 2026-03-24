namespace BuildDuty.AI;

/// <summary>
/// A streaming event from an AI agent session, used for live
/// rendering of agent activity in the terminal.
/// </summary>
public sealed record AgentStreamEvent
{
    /// <summary>Event type: delta, tool-start, tool-end, message, error.</summary>
    public required string Type { get; init; }

    /// <summary>Text content (for delta, message, error).</summary>
    public string? Content { get; init; }

    /// <summary>Tool name (for tool-start).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool arguments JSON (for tool-start).</summary>
    public string? ToolArgs { get; init; }

    /// <summary>Whether the tool succeeded (for tool-end).</summary>
    public bool? ToolSuccess { get; init; }
}
