using System.Diagnostics.CodeAnalysis;
using Octokit;

namespace BuildDuty.Core;

public sealed class GitHubIssueSignal : Signal<Issue>
{
    public override SignalType Type => SignalType.GitHubIssue;

    [SetsRequiredMembers]
    public GitHubIssueSignal(Issue issue)
    {
        if (issue == null)
        {
            throw new ArgumentNullException(nameof(issue));
        }
        Info = issue;
    }
}

public sealed class GitHubPullRequestSignal : Signal<PullRequest>
{
    public override SignalType Type => SignalType.GitHubPullRequest;

    [SetsRequiredMembers]
    public GitHubPullRequestSignal(PullRequest pullRequest)
    {
        if (pullRequest == null)
        {
            throw new ArgumentNullException(nameof(pullRequest));
        }
        Info = pullRequest;
    }
}
