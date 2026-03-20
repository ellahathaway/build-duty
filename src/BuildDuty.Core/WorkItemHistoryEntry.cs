using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class WorkItemHistoryEntry
{
    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("actor")]
    public string Actor { get; set; } = "build-duty";

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}
