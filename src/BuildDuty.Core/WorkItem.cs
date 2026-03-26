using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed class WorkItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("signals")]
    public List<ISignal> Signals { get; set; } = [];
}
