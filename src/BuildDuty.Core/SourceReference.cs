using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class SourceReference
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    /// <summary>When the source was last modified (build finish, issue/PR updated_at).</summary>
    [JsonPropertyName("sourceUpdatedAtUtc")]
    public DateTime? SourceUpdatedAtUtc { get; set; }

    /// <summary>Additional metadata carried from collection (e.g. stageFilters).</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}
