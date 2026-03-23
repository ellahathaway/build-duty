using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class WorkItem
{
    /// <summary>Terminal statuses — items in these statuses are considered resolved.</summary>
    public static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "resolved", "fixed", "merged", "closed",
    };

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Type-specific status (e.g. "new", "needs-review", "tracked", "test-failures", "fixed").
    /// Terminal statuses (resolved, fixed, merged, closed) indicate the item is done.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "new";

    /// <summary>
    /// AI-generated summary of the work item's source (build failure reason,
    /// issue description, PR changes, etc.).
    /// </summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>
    /// When the summary was last written. Used to detect stale summaries —
    /// if the source has been updated since this timestamp, re-summarize.
    /// </summary>
    [JsonPropertyName("summarizedAtUtc")]
    public DateTime? SummarizedAtUtc { get; set; }

    /// <summary>
    /// When the item was last triaged (status/links updated). Used to skip
    /// items that haven't changed since the last triage run.
    /// </summary>
    [JsonPropertyName("triagedAtUtc")]
    public DateTime? TriagedAtUtc { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// IDs of related work items (cross-referenced during correlation).
    /// </summary>
    [JsonPropertyName("linkedItems")]
    public List<string> LinkedItems { get; set; } = [];

    [JsonPropertyName("signals")]
    public List<SignalReference> Signals { get; set; } = [];

    [JsonPropertyName("history")]
    public List<WorkItemHistoryEntry> History { get; set; } = [];

    /// <summary>Whether the item is in a terminal status.</summary>
    [JsonIgnore]
    public bool IsResolved => TerminalStatuses.Contains(Status);

    /// <summary>
    /// Whether the summary needs to be written or refreshed.
    /// True if there's no summary, or if the source has been updated since the last summary.
    /// </summary>
    [JsonIgnore]
    public bool NeedsSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Summary))
                return true;

            if (!SummarizedAtUtc.HasValue)
                return true;

            // Check if any signal source has been updated since the last summary
            return Signals.Any(s =>
                s.SourceUpdatedAtUtc.HasValue &&
                s.SourceUpdatedAtUtc.Value > SummarizedAtUtc.Value);
        }
    }

    /// <summary>
    /// Whether the item needs triage. True if never triaged, or if the
    /// summary has been written/refreshed since the last triage.
    /// </summary>
    [JsonIgnore]
    public bool NeedsTriage
    {
        get
        {
            if (!TriagedAtUtc.HasValue)
                return true;

            // Re-triage if the summary was updated since the last triage
            return SummarizedAtUtc.HasValue && SummarizedAtUtc.Value > TriagedAtUtc.Value;
        }
    }

    /// <summary>
    /// Update the status and append a history entry.
    /// </summary>
    public void SetStatus(string newStatus, string? note = null, string actor = "build-duty")
    {
        var old = Status;
        Status = newStatus;
        History.Add(new WorkItemHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Action = "status-change",
            From = old,
            To = newStatus,
            Actor = actor,
            Note = note,
        });
    }
}
