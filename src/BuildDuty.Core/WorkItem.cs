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
    /// Collection state — set by collectors to describe what was observed.
    /// Triage reads this to decide status changes.
    /// Values: "new" (first collected), "updated" (source changed), "closed" (source no longer active).
    /// Null means no pending state change (already processed by triage).
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

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

    [JsonPropertyName("sources")]
    public List<SourceReference> Sources { get; set; } = [];

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
            // Closed or stable items don't need summarization
            if (State is "closed" or "stable")
                return false;

            if (string.IsNullOrWhiteSpace(Summary))
                return true;

            if (!SummarizedAtUtc.HasValue)
                return true;

            // Check if any source has been updated since the last summary
            return Sources.Any(s =>
                s.SourceUpdatedAtUtc.HasValue &&
                s.SourceUpdatedAtUtc.Value > SummarizedAtUtc.Value);
        }
    }

    /// <summary>
    /// Whether the item needs triage. True if never triaged, or if the
    /// summary has been written/refreshed since the last triage.
    /// Acknowledged items only re-triage when state is "closed" (so triage can resolve them).
    /// </summary>
    [JsonIgnore]
    public bool NeedsTriage
    {
        get
        {
            // Stable items don't need triage
            if (State == "stable")
                return false;

            // Acknowledged items ignore updates — only triage on "closed" (to resolve)
            if (Status == "acknowledged")
                return State == "closed";

            // Items with a pending collection state need triage
            if (State is not null)
                return true;

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
