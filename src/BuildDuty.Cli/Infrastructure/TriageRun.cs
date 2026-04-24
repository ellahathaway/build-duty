using System.Text.Json.Serialization;

namespace BuildDuty.Cli.Infrastructure;

public sealed record TriageRun
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = IdGenerator.NewTriageRunId();

    [JsonPropertyName("timeStarted")]
    public DateTime TimeStarted { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("signalsXmlPath")]
    public string SignalsXmlPath { get; set; } = string.Empty;

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
