using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class SignalReference
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    /// <summary>Additional metadata carried from collection (e.g. stageFilters).</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}
