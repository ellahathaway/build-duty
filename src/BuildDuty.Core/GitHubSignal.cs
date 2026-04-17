using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

/// <summary>
/// Shared info for both GitHub issues and pull requests.
/// </summary>
public record GitHubItemInfo(
    int Number,
    string Title,
    string State,
    DateTimeOffset? UpdatedAt,
    string? Body,
    List<string>? Comments);

/// <summary>
/// A cross-referenced or connected PR event from the issue timeline.
/// </summary>
public record GitHubTimelineEvent(
    string Event,
    string? SourceUrl,
    string? SourceState);

/// <summary>
/// Issue-specific info including timeline events for cross-referenced PRs and issues.
/// </summary>
public record GitHubIssueInfo(GitHubItemInfo ItemInfo, List<GitHubTimelineEvent>? TimelineEvents = null);

public record GitHubPullRequestInfo(GitHubItemInfo ItemInfo, bool Merged, List<GitHubCheckInfo>? Checks);

public record GitHubCheckInfo(
    string Name,
    string Status,
    string? Conclusion);

public sealed class GitHubIssueSignal : Signal
{
    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(GitHubIssueInfo typedInfo, Uri url)
    {
        Info = JsonSerializer.SerializeToElement(typedInfo);
        Url = url;
    }

    public GitHubIssueSignal() { }

    [JsonIgnore]
    public GitHubIssueInfo TypedInfo => JsonSerializer.Deserialize<GitHubIssueInfo>(Info.GetRawText())!;
}

public sealed class GitHubPullRequestSignal : Signal
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(GitHubPullRequestInfo typedInfo, Uri url)
    {
        Info = JsonSerializer.SerializeToElement(typedInfo);
        Url = url;
    }

    public GitHubPullRequestSignal() { }

    [JsonIgnore]
    public GitHubPullRequestInfo TypedInfo => JsonSerializer.Deserialize<GitHubPullRequestInfo>(Info.GetRawText())!;
}
