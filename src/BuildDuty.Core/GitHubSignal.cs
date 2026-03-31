using Octokit;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace BuildDuty.Core;

public sealed class GitHubIssueSignal : Signal
{
    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(Issue issue)
    {
        TypedInfo = issue;
    }

    public GitHubIssueSignal() { }

    [System.Text.Json.Serialization.JsonIgnore]
    public Issue TypedInfo
    {
        get => Newtonsoft.Json.JsonConvert.DeserializeObject<Issue>(Info.GetRawText())!;
        set => Info = System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}

public sealed class GitHubPullRequestSignal : Signal
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(PullRequest pr)
    {
        TypedInfo = pr;
    }

    public GitHubPullRequestSignal() { }

    [System.Text.Json.Serialization.JsonIgnore]
    public PullRequest TypedInfo
    {
        get => Newtonsoft.Json.JsonConvert.DeserializeObject<PullRequest>(Info.GetRawText())!;
        set => Info = System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}
