using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public record GitHubIssueInfo(
    int Number,
    string Title,
    string State,
    DateTimeOffset? UpdatedAt,
    string? Body,
    List<string>? Comments);

public record GitHubPullRequestInfo(
    int Number,
    string Title,
    string State,
    DateTimeOffset? UpdatedAt,
    bool Merged,
    string? Body,
    List<string>? Comments,
    List<GitHubCheckInfo>? Checks);

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
