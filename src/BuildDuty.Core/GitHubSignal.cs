using Octokit;

namespace BuildDuty.Core;

public abstract class GitHubSignal<TInfo> : Signal<GitHubSignalType, TInfo> where TInfo : class;

public sealed class GitHubIssueSignal : GitHubSignal<Issue>
{
    public override GitHubSignalType Type => GitHubSignalType.Issue;

    public static GitHubIssueSignal Create(
        Issue issue,
        List<string>? workItemIds = null)
    {
        return new GitHubIssueSignal
        {
            Info = issue,
            WorkItemIds = workItemIds ?? [],
        };
    }
}

public sealed class GitHubPullRequestSignal : GitHubSignal<PullRequest>
{
    public override GitHubSignalType Type => GitHubSignalType.PullRequest;

    public static GitHubPullRequestSignal Create(
        PullRequest pr,
        List<string>? workItemIds = null)
    {
        return new GitHubPullRequestSignal
        {
            Info = pr,
            WorkItemIds = workItemIds ?? [],
        };
    }
}

public enum GitHubSignalType
{
    Issue,
    PullRequest,
}
