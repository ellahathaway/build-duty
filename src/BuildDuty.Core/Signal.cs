using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public abstract class Signal
{
    public string Id { get; set; } = IdGenerator.NewSignalId();

    public abstract SignalType Type { get; }

    public required Uri Url { get; set; }

    public required JsonElement Info { get; set; }

    public string? Context { get; set; }

    public List<SignalAnalysis> Analyses { get; set; } = new();

    /// <summary>
    /// Copies identity and existing analyses from a previous version of this signal.
    /// Call this when updating a signal that was already collected.
    /// </summary>
    public void PreserveFrom(Signal existing)
    {
        Id = existing.Id;
        Analyses = existing.Analyses;
    }
}

public record SignalAnalysis
{
    [JsonConstructor]
    public SignalAnalysis(string id, JsonElement relevantInfo, string analysis)
    {
        Id = id;
        RelevantInfo = relevantInfo;
        Analysis = analysis;
    }

    public SignalAnalysis(JsonElement relevantInfo, string analysis)
        : this(IdGenerator.NewAnalysisId(), relevantInfo, analysis) { }

    public string Id { get; init; }
    public JsonElement RelevantInfo { get; init; }
    public string Analysis { get; init; }
}

public enum SignalType
{
    GitHubIssue,
    GitHubPullRequest,
    AzureDevOpsPipeline
}
