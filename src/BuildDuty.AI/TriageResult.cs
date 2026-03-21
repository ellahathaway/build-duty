using System.Text.Json.Serialization;

namespace BuildDuty.AI;

/// <summary>
/// Result of a single AI triage invocation against a work item.
/// </summary>
public sealed class TriageResult
{
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("workItemId")]
    public required string WorkItemId { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; init; }

    [JsonPropertyName("finishedUtc")]
    public DateTime FinishedUtc { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => FinishedUtc - StartedUtc;
}
