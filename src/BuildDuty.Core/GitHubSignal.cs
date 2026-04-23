using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Octokit;

namespace BuildDuty.Core;

public record GitHubItemInfo(
    int Number,
    string Title,
    string State,
    DateTimeOffset? UpdatedAt,
    string? Body,
    List<string>? Comments,
    List<GitHubTimelineEvent>? TimelineEvents = null);

public record GitHubTimelineEvent(
    string Event,
    string? SourceUrl,
    string? SourceState);

public record GitHubIssueInfo(GitHubItemInfo ItemInfo);

public record GitHubPullRequestInfo(GitHubItemInfo ItemInfo, bool Merged, List<GitHubCheckInfo>? Checks);

public record GitHubCheckInfo(
    string Name,
    string Status,
    string? Conclusion);

public abstract class GitHubSignal : Signal
{
    [JsonIgnore]
    public abstract GitHubItemInfo ItemInfo { get; }

    public static GitHubSignal Create(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras, Uri url, string? context = null)
    {
        if (issue.PullRequest != null)
        {
            return new GitHubPullRequestSignal(issue, comments, timelineEvents, extras?.GetProperty("Checks").Deserialize<List<GitHubCheckInfo>>(), url, context);
        }
        else
        {
            return new GitHubIssueSignal(issue, comments, timelineEvents, url, context);
        }
    }

    protected static GitHubItemInfo BuildItemInfo(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents)
        => new(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt, issue.Body, comments, timelineEvents);

    protected abstract JsonElement CreateInfoElement(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras = null);

    public void AsUpdated(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras, Uri url, string? context = null)
        => AsUpdated(CreateInfoElement(issue, comments, timelineEvents, extras), url, context);

    public void AsResolved(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras, Uri url, string? context = null)
        => AsResolved(CreateInfoElement(issue, comments, timelineEvents, extras), url, context);

    public void AsNew(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras, Uri url, string? context = null)
        => AsNew(CreateInfoElement(issue, comments, timelineEvents, extras), url, context);
}

public sealed class GitHubIssueSignal : GitHubSignal
{
    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, Uri url, string? context = null)
    {
        Info = CreateInfoElement(issue, comments, timelineEvents);
        Url = url;
        Context = context;
    }

    [SetsRequiredMembers]
    internal GitHubIssueSignal(GitHubIssueInfo typedInfo, Uri url)
    {
        Info = JsonSerializer.SerializeToElement(typedInfo);
        Url = url;
    }

    public GitHubIssueSignal() { }

    [JsonIgnore]
    public GitHubIssueInfo TypedInfo => JsonSerializer.Deserialize<GitHubIssueInfo>(Info.GetRawText())!;

    [JsonIgnore]
    public override GitHubItemInfo ItemInfo => TypedInfo.ItemInfo;

    protected override JsonElement CreateInfoElement(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras = null)
        => JsonSerializer.SerializeToElement(new GitHubIssueInfo(BuildItemInfo(issue, comments, timelineEvents)));
}

public sealed class GitHubPullRequestSignal : GitHubSignal
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, List<GitHubCheckInfo>? checks, Uri url, string? context = null)
    {
        Info = CreateInfoElement(issue, comments, timelineEvents, JsonSerializer.SerializeToElement(new { Merged = issue.PullRequest?.Merged ?? false, Checks = checks }));
        Url = url;
        Context = context;
    }

    [SetsRequiredMembers]
    internal GitHubPullRequestSignal(GitHubPullRequestInfo typedInfo, Uri url)
    {
        Info = JsonSerializer.SerializeToElement(typedInfo);
        Url = url;
    }

    public GitHubPullRequestSignal() { }

    [JsonIgnore]
    public GitHubPullRequestInfo TypedInfo => JsonSerializer.Deserialize<GitHubPullRequestInfo>(Info.GetRawText())!;

    [JsonIgnore]
    public override GitHubItemInfo ItemInfo => TypedInfo.ItemInfo;

    protected override JsonElement CreateInfoElement(Issue issue, List<string>? comments, List<GitHubTimelineEvent>? timelineEvents, JsonElement? extras = null)
    {
        var checks = extras?.GetProperty("Checks").Deserialize<List<GitHubCheckInfo>>() ?? throw new InvalidOperationException("Checks property is missing");
        bool merged = extras?.GetProperty("Merged").GetBoolean() ?? throw new InvalidOperationException("Merged property is missing");
        return JsonSerializer.SerializeToElement(new GitHubPullRequestInfo(BuildItemInfo(issue, comments, timelineEvents), merged, checks));
    }
}
