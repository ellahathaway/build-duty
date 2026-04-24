using System.Text.Json.Serialization;

namespace BuildDuty.Cli.Infrastructure;

public record LinkedAnalysis(string SignalId, List<string> AnalysisIds);

public sealed class WorkItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("linkedAnalyses")]
    public List<LinkedAnalysis> LinkedAnalyses { get; set; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("issueSignature")]
    public string? IssueSignature { get; set; }

    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("lastTriageId")]
    public string? LastTriageId { get; set; }

    [JsonPropertyName("resolvedAt")]
    public DateTime? ResolvedAt { get; set; }
}
