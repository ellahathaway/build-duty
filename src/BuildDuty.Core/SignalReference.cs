using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class SignalReference
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;
}
