using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class WorkItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public WorkItemState State { get; set; } = WorkItemState.Unresolved;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("signals")]
    public List<SignalReference> Signals { get; set; } = [];

    [JsonPropertyName("history")]
    public List<WorkItemHistoryEntry> History { get; set; } = [];

    /// <summary>
    /// Transition to a new state. Validates the transition and appends history.
    /// </summary>
    public void TransitionTo(WorkItemState newState, string? note = null, string actor = "build-duty")
    {
        ValidateTransition(State, newState);
        var entry = new WorkItemHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Action = "state-change",
            From = State.ToString().ToLowerInvariant(),
            To = newState.ToString().ToLowerInvariant(),
            Actor = actor,
            Note = note
        };
        History.Add(entry);
        State = newState;
    }

    internal static void ValidateTransition(WorkItemState from, WorkItemState to)
    {
        bool valid = (from, to) switch
        {
            (WorkItemState.Unresolved, WorkItemState.InProgress) => true,
            (WorkItemState.InProgress, WorkItemState.Resolved) => true,
            (WorkItemState.InProgress, WorkItemState.Unresolved) => true, // return on failure
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Invalid state transition from '{from}' to '{to}'.");
        }
    }
}
