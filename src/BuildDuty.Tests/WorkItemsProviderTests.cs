using BuildDuty.Core;
using NSubstitute;
using Octokit;
using Xunit;

namespace BuildDuty.Tests;

public class WorkItemsProviderTests
{
    private static readonly User TestUser = new();

    private static Issue CreateIssue(int number, ItemState state) => new(
        url: "", htmlUrl: $"https://github.com/org/repo/issues/{number}",
        commentsUrl: "", eventsUrl: "",
        number: number, state: state, title: "Issue", body: "",
        closedBy: null, user: TestUser, labels: [], assignee: null, assignees: [],
        milestone: null, comments: 0, pullRequest: null,
        closedAt: null, createdAt: DateTimeOffset.Now, updatedAt: null,
        id: number, nodeId: "", locked: false, repository: null,
        reactions: null, stateReason: null, activeLockReason: null);

    private static PullRequest CreatePullRequest(int number, ItemState state) => new(
        id: number, nodeId: "", url: "",
        htmlUrl: $"https://github.com/org/repo/pull/{number}",
        diffUrl: "", patchUrl: "", issueUrl: "", statusesUrl: "",
        number: number, state: state, title: "PR", body: "",
        createdAt: DateTimeOffset.Now, updatedAt: DateTimeOffset.Now,
        closedAt: null, mergedAt: null,
        head: null, @base: null, user: TestUser,
        assignee: null, assignees: [], draft: false,
        mergeable: null, mergeableState: null, mergedBy: null,
        mergeCommitSha: "", comments: 0, commits: 0,
        additions: 0, deletions: 0, changedFiles: 0,
        milestone: null, locked: false, maintainerCanModify: null,
        requestedReviewers: [], requestedTeams: [], labels: [],
        activeLockReason: null);

    private static IWorkItemsProvider CreateMockProvider(params WorkItem[] items)
    {
        var provider = Substitute.For<IWorkItemsProvider>();
        provider.GetWorkItemsAsync(Arg.Any<Enum?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var signalType = callInfo.ArgAt<Enum?>(0);
                if (signalType is null)
                    return items.AsEnumerable();
                return items.Where(wi => wi.Signals.Any(s => s.Type.Equals(signalType)));
            });
        return provider;
    }

    [Fact]
    public async Task EmptyProvider_ReturnsEmpty()
    {
        var provider = CreateMockProvider();
        var items = await provider.GetWorkItemsAsync();
        Assert.Empty(items);
    }

    [Fact]
    public async Task ReturnsAllWorkItems()
    {
        var wi1 = new WorkItem { Id = "wi-issue", Signals = [GitHubIssueSignal.Create(CreateIssue(1, ItemState.Open))] };
        var wi2 = new WorkItem { Id = "wi-pr", Signals = [GitHubPullRequestSignal.Create(CreatePullRequest(2, ItemState.Open))] };

        var provider = CreateMockProvider(wi1, wi2);
        var all = (await provider.GetWorkItemsAsync()).ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task FiltersBySignalType()
    {
        var wi1 = new WorkItem { Id = "wi-issue", Signals = [GitHubIssueSignal.Create(CreateIssue(1, ItemState.Open))] };
        var wi2 = new WorkItem { Id = "wi-pr", Signals = [GitHubPullRequestSignal.Create(CreatePullRequest(2, ItemState.Open))] };

        var provider = CreateMockProvider(wi1, wi2);

        var issues = (await provider.GetWorkItemsAsync(GitHubSignalType.Issue)).ToList();
        Assert.Single(issues);
        Assert.Equal("wi-issue", issues[0].Id);

        var prs = (await provider.GetWorkItemsAsync(GitHubSignalType.PullRequest)).ToList();
        Assert.Single(prs);
        Assert.Equal("wi-pr", prs[0].Id);
    }

    [Fact]
    public async Task ExistingSignals_PreserveWorkItemIds()
    {
        var signal = GitHubIssueSignal.Create(CreateIssue(42, ItemState.Open), ["wi-linked"]);
        var wi = new WorkItem { Id = "wi-with-links", Signals = [signal] };

        var provider = CreateMockProvider(wi);
        var items = (await provider.GetWorkItemsAsync(GitHubSignalType.Issue)).ToList();
        var loaded = items[0].Signals.OfType<GitHubIssueSignal>().First();

        Assert.Equal(["wi-linked"], loaded.WorkItemIds);
    }
}
