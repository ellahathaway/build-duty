using System.Diagnostics.CodeAnalysis;
using Octokit;

namespace BuildDuty.Core;

public sealed class GitHubIssueSignal : Signal<Issue>
{
    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(Issue info)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }
        Info = info;
    }
}

public sealed class GitHubPullRequestSignal : Signal<PullRequest>
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(PullRequest info)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }
        Info = info;
    }
}
