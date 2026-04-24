using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace BuildDuty.Signals;

/// <summary>
/// Base class for all signals. XML-serializable with polymorphic support
/// via <see cref="XmlIncludeAttribute"/>.
/// </summary>
[XmlInclude(typeof(AzureDevOpsPipelineSignal))]
[XmlInclude(typeof(GitHubIssueSignal))]
[XmlInclude(typeof(GitHubPullRequestSignal))]
public abstract class Signal
{
    [XmlIgnore]
    public abstract SignalType Type { get; }

    [XmlAttribute("Type")]
    public string TypeName => Type.ToString();

    [XmlAttribute]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute]
    public string Url { get; set; } = string.Empty;

    [XmlElement]
    public string? Context { get; set; }

    [XmlAttribute]
    public SignalCollectionReason CollectionReason { get; set; } = SignalCollectionReason.New;

    [XmlArray("Analyses")]
    [XmlArrayItem("Analysis")]
    public List<SignalAnalysis> Analyses { get; set; } = [];

    [JsonIgnore]
    [XmlIgnore]
    public bool IsResolvedCollectionReason =>
        CollectionReason is SignalCollectionReason.Resolved
            or SignalCollectionReason.NotFound
            or SignalCollectionReason.OutOfScope;
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
        : this($"analysis_{Guid.NewGuid():N}", relevantInfo, analysis) { }

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
