using Octokit;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace BuildDuty.Core;

public sealed class GitHubIssueSignal : Signal
{
    private Issue? _typedInfo;

    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(Issue issue)
    {
        TypedInfo = issue;
    }

    public GitHubIssueSignal() { }

    [JsonIgnore]
    public Issue TypedInfo
    {
        get => _typedInfo ??= Newtonsoft.Json.JsonConvert.DeserializeObject<Issue>(Info.GetRawText())!;
        set
        {
            _typedInfo = value;
            Info = JsonSerializer.SerializeToElement(value);
        }
    }
}

public sealed class GitHubPullRequestSignal : Signal
{
    private PullRequest? _typedInfo;

    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(PullRequest pr)
    {
        TypedInfo = pr;
    }

    public GitHubPullRequestSignal() { }

    [JsonIgnore]
    public PullRequest TypedInfo
    {
        get => _typedInfo ??= Newtonsoft.Json.JsonConvert.DeserializeObject<PullRequest>(Info.GetRawText())!;
        set
        {
            _typedInfo = value;
            Info = JsonSerializer.SerializeToElement(value);
        }
    }
}
