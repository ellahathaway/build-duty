using System.Xml.Serialization;

namespace BuildDuty.Signals;

/// <summary>
/// Base class for GitHub signals (issues and pull requests).
/// </summary>
public abstract class GitHubSignal : Signal
{
    [XmlAttribute]
    public string Organization { get; set; } = string.Empty;

    [XmlAttribute]
    public string Repository { get; set; } = string.Empty;

    [XmlElement("Item")]
    public GitHubItemInfo Item { get; set; } = new();
}

public sealed class GitHubItemInfo
{
    [XmlAttribute]
    public int Number { get; set; }

    [XmlAttribute]
    public string Title { get; set; } = string.Empty;

    [XmlAttribute]
    public string State { get; set; } = string.Empty;

    [XmlAttribute]
    public string? UpdatedAt { get; set; }

    [XmlElement]
    public string? Body { get; set; }

    [XmlArray("Comments")]
    [XmlArrayItem("Comment")]
    public List<string>? Comments { get; set; }

    [XmlArray("TimelineEvents")]
    [XmlArrayItem("Event")]
    public List<GitHubTimelineEventInfo>? TimelineEvents { get; set; }
}

public sealed class GitHubTimelineEventInfo
{
    [XmlAttribute]
    public string Event { get; set; } = string.Empty;

    [XmlAttribute]
    public string? SourceUrl { get; set; }

    [XmlAttribute]
    public string? SourceState { get; set; }
}

/// <summary>
/// Signal for a GitHub issue.
/// </summary>
public sealed class GitHubIssueSignal : GitHubSignal
{
    public override SignalType Type => SignalType.GitHubIssue;
}

/// <summary>
/// Signal for a GitHub pull request.
/// </summary>
public sealed class GitHubPullRequestSignal : GitHubSignal
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [XmlAttribute]
    public bool Merged { get; set; }

    [XmlArray("Checks")]
    [XmlArrayItem("Check")]
    public List<GitHubCheckInfo>? Checks { get; set; }
}

public sealed class GitHubCheckInfo
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Status { get; set; } = string.Empty;

    [XmlAttribute]
    public string? Conclusion { get; set; }
}
