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
    public string Url { get; set; } = string.Empty;

    [XmlElement]
    public string? Context { get; set; }
}

public enum SignalType
{
    GitHubIssue,
    GitHubPullRequest,
    AzureDevOpsPipeline
}
