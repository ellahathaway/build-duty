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

    public SignalCollectionReason CollectionReason { get; set; } = SignalCollectionReason.New;

    public List<SignalAnalysis> Analyses { get; set; } = new();

    [JsonIgnore]
    public bool IsResolvedCollectionReason => CollectionReason == SignalCollectionReason.Resolved || CollectionReason == SignalCollectionReason.NotFound || CollectionReason == SignalCollectionReason.OutOfScope;

    public void AsUpdated(JsonElement info, Uri url, string? context = null)
    {
        Info = info;
        Url = url;
        CollectionReason = SignalCollectionReason.Updated;

        if (context != null)
        {
            Context = context;
        }
    }

    public void AsResolved(JsonElement info, Uri url, string? context = null)
    {
        Info = info;
        Url = url;
        CollectionReason = SignalCollectionReason.Resolved;

        if (context != null)
        {
            Context = context;
        }
    }

    public void AsNotFound(string? context = null)
    {
        CollectionReason = SignalCollectionReason.NotFound;

        if (context != null)
        {
            Context = context;
        }
    }

    public void AsOutOfScope()
    {
        CollectionReason = SignalCollectionReason.OutOfScope;
    }

    public void AsNew(JsonElement info, Uri url, string? context = null)
    {
        Info = info;
        Url = url;
        CollectionReason = SignalCollectionReason.New;

        if (context != null)
        {
            Context = context;
        }
    }
}

public record SignalAnalysis
{
    [JsonConstructor]
    public SignalAnalysis(
        string id,
        JsonElement relevantInfo,
        string analysis,
        AnalysisStatus status = AnalysisStatus.New,
        string? resolutionReason = null,
        string? lastTriageId = null)
    {
        Id = id;
        RelevantInfo = relevantInfo;
        Analysis = analysis;
        Status = status;
        ResolutionReason = resolutionReason;
        LastTriageId = lastTriageId;
    }

    public SignalAnalysis(JsonElement relevantInfo, string analysis)
        : this(IdGenerator.NewAnalysisId(), relevantInfo, analysis) { }

    public string Id { get; init; }
    public JsonElement RelevantInfo { get; init; }
    public string Analysis { get; init; }
    public AnalysisStatus Status { get; init; } = AnalysisStatus.New;
    public string? ResolutionReason { get; init; }
    public string? LastTriageId { get; init; }
}

public enum AnalysisStatus
{
    New,
    Updated,
    Resolved
}

public enum SignalType
{
    GitHubIssue,
    GitHubPullRequest,
    AzureDevOpsPipeline
}

public enum SignalCollectionReason
{
    New,
    Updated,
    Resolved,
    NotFound,
    OutOfScope
}
