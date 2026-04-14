using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public sealed record TriageRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = IdGenerator.NewTriageRunId();

    [JsonPropertyName("timeStarted")]
    public DateTime TimeStarted { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("signalIds")]
    public List<string> SignalIds { get; set; } = new List<string>();

    [JsonPropertyName("status")]
    public TriageRunStatus Status { get; set; } = TriageRunStatus.NotStarted;
}

public enum TriageRunStatus
{
    NotStarted,
    CollectingSignals,
    AnalyzingSignals,
    UpdatingWorkItems,
    CreatingWorkItems,
    Done
}
