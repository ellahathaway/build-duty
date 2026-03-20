using System.Text.Json.Serialization;

namespace BuildDuty.AI;

public sealed class AiRunResult
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("workItemId")]
    public string WorkItemId { get; set; } = string.Empty;

    [JsonPropertyName("job")]
    public string Job { get; set; } = string.Empty;

    [JsonPropertyName("skill")]
    public string Skill { get; set; } = string.Empty;

    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; set; }

    [JsonPropertyName("finishedUtc")]
    public DateTime FinishedUtc { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("stderr")]
    public string? Stderr { get; set; }
}
