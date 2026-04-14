using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class WorkItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("signalIds")]
    public List<string> SignalIds { get; set; } = [];

    [JsonPropertyName("triageId")]
    public string? TriageId { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("issueSignature")]
    public string? IssueSignature { get; set; }

    [JsonPropertyName("correlationRationale")]
    public string? CorrelationRationale { get; set; }

    [JsonPropertyName("resolutionCriteria")]
    public string? ResolutionCriteria { get; set; }

    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }

    [JsonPropertyName("resolutionReason")]
    public string? ResolutionReason { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("resolvedAt")]
    public DateTime? ResolvedAt { get; set; }
}
