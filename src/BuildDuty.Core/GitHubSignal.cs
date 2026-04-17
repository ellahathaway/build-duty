using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public record GitHubLinkedPullRequest(
    string Url,
    int Number,
    string Repository,
    string State,
    bool Merged);

public record GitHubIssueInfo(
    int Number,
    string Title,
    string State,
    DateTimeOffset? UpdatedAt,
    string? Body,
    List<string>? Comments,
    List<GitHubLinkedPullRequest>? LinkedPullRequests = null);

public record GitHubPullRequestInfo(GitHubIssueInfo IssueInfo, bool Merged, List<GitHubCheckInfo>? Checks);

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
