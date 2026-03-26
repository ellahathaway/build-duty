using System.Text.RegularExpressions;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Maestro.Common;
using NSubstitute;
using Octokit;
using Xunit;

namespace BuildDuty.Tests;

public class GitHubSignalTests
{
    private static readonly User TestUser = new();

    private static Issue CreateIssue(int number, ItemState state, string title = "Test issue", bool isPr = false, DateTimeOffset? updatedAt = null) => new(
        url: $"https://api.github.com/repos/dotnet/runtime/issues/{number}",
        htmlUrl: $"https://github.com/dotnet/runtime/issues/{number}",
        commentsUrl: "", eventsUrl: "",
        number: number, state: state, title: title, body: "",
        closedBy: null, user: TestUser, labels: [], assignee: null, assignees: [],
        milestone: null, comments: 0,
        pullRequest: isPr ? new PullRequest() : null,
        closedAt: null, createdAt: DateTimeOffset.Now, updatedAt: updatedAt,
        id: number, nodeId: "", locked: false, repository: null,
        reactions: null, stateReason: null, activeLockReason: null);

    private static PullRequest CreatePullRequest(int number, ItemState state, bool merged = false, string title = "Test PR", DateTimeOffset? updatedAt = null) => new(
        id: number, nodeId: "",
        url: $"https://api.github.com/repos/dotnet/sdk/pulls/{number}",
        htmlUrl: $"https://github.com/dotnet/sdk/pull/{number}",
        diffUrl: "", patchUrl: "", issueUrl: "", statusesUrl: "",
        number: number, state: state, title: title, body: "",
        createdAt: DateTimeOffset.Now, updatedAt: updatedAt ?? DateTimeOffset.Now,
        closedAt: null, mergedAt: merged ? DateTimeOffset.Now : null,
        head: null, @base: null, user: TestUser,
        assignee: null, assignees: [], draft: false,
        mergeable: null, mergeableState: null, mergedBy: null,
        mergeCommitSha: "", comments: 0, commits: 0,
        additions: 0, deletions: 0, changedFiles: 0,
        milestone: null, locked: false, maintainerCanModify: null,
        requestedReviewers: [], requestedTeams: [], labels: [],
        activeLockReason: null);

    private static GitHubConfig CreateIssueConfig(string org = "dotnet", string repo = "runtime", List<string>? labels = null) => new()
    {
        Organizations =
        [
            new GitHubOrganizationConfig
            {
                Organization = org,
                Repositories =
                [
                    new GitHubRepositoryConfig
                    {
                        Name = repo,
                        Issues = new GitHubIssueConfig
                        {
                            Labels = labels ?? ["bug"],
                            State = ItemStateFilter.Open,
                        }
                    }
                ]
            }
        ]
    };

    private static GitHubConfig CreatePrConfig(string org = "dotnet", string repo = "sdk", List<GitHubPullRequestPattern>? patterns = null) => new()
    {
        Organizations =
        [
            new GitHubOrganizationConfig
            {
                Organization = org,
                Repositories =
                [
                    new GitHubRepositoryConfig
                    {
                        Name = repo,
                        PullRequests = patterns ?? [new GitHubPullRequestPattern { Name = new Regex(".*"), State = ItemStateFilter.Open }]
                    }
                ]
            }
        ]
    };

    private static GitHubConfig CreateMixedConfig() => new()
    {
        Organizations =
        [
            new GitHubOrganizationConfig
            {
                Organization = "dotnet",
                Repositories =
                [
                    new GitHubRepositoryConfig
                    {
                        Name = "runtime",
                        Issues = new GitHubIssueConfig { Labels = ["bug"], State = ItemStateFilter.Open },
                        PullRequests = [new GitHubPullRequestPattern { Name = new Regex("^Update"), State = ItemStateFilter.Open }]
                    }
                ]
            }
        ]
    };

    private class TestableGitHubCollector : GitHubSignalCollector
    {
        private readonly IGitHubClient _client;

        public TestableGitHubCollector(
            GitHubConfig config,
            IWorkItemsProvider workItemsProvider,
            IGitHubClient client)
            : base(config, Substitute.For<IRemoteTokenProvider>(), workItemsProvider)
        {
            _client = client;
        }

        protected override Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
        {
            return Task.FromResult(new RepositoryContext
            {
                Organization = organization,
                RepositoryName = repository,
                Client = _client,
            });
        }
    }

    [Fact]
    public async Task CollectAsync_NewIssues_NoExistingWorkItems_CreatesSignals()
    {
        var issue1 = CreateIssue(1, ItemState.Open, "Bug A");
        var issue2 = CreateIssue(2, ItemState.Open, "Bug B");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue1, issue2]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(Arg.Any<Enum>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        Assert.Equal(2, signals.Count);
        Assert.All(signals, s =>
        {
            var issueSignal = Assert.IsType<GitHubIssueSignal>(s);
            Assert.Empty(issueSignal.WorkItemIds);
        });
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_SameState_SkipsSignal()
    {
        var issue = CreateIssue(1, ItemState.Open);
        var existingSignal = GitHubIssueSignal.Create(issue, ["wi-1"]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-1", Signals = [existingSignal] }]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        Assert.Empty(signals);
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_DifferentState_CollectsWithPreservedWorkItemIds()
    {
        var openIssue = CreateIssue(1, ItemState.Open);
        var existingSignal = GitHubIssueSignal.Create(openIssue, ["wi-1", "wi-2"]);

        var closedIssue = CreateIssue(1, ItemState.Closed);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([closedIssue]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-1", Signals = [existingSignal] }]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(ItemState.Closed, issueSignal.Info.State.Value);
        Assert.Equal(["wi-1", "wi-2"], issueSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_IssuesThatArePullRequests_AreFiltered()
    {
        var realIssue = CreateIssue(1, ItemState.Open, "Real issue");
        var prAsIssue = CreateIssue(2, ItemState.Open, "Actually a PR", isPr: true);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([realIssue, prAsIssue]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(Arg.Any<Enum>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal("Real issue", issueSignal.Info.Title);
    }

    [Fact]
    public async Task CollectAsync_PullRequests_OnlyMatchingPatternsCollected()
    {
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update dependencies");
        var noMatchPr = CreatePullRequest(2, ItemState.Open, title: "Fix something");

        var patterns = new List<GitHubPullRequestPattern>
        {
            new() { Name = new Regex("^Update"), State = ItemStateFilter.Open }
        };

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([matchPr, noMatchPr]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(Arg.Any<Enum>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreatePrConfig(patterns: patterns), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal("Update dependencies", prSignal.Info.Title);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_SkipsSignal()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps");
        var existingSignal = GitHubPullRequestSignal.Create(pr, ["wi-3"]);

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([pr]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-3", Signals = [existingSignal] }]);

        var collector = new TestableGitHubCollector(CreatePrConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        Assert.Empty(signals);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_DifferentState_CollectsWithPreservedWorkItemIds()
    {
        var openPr = CreatePullRequest(1, ItemState.Open, title: "Update deps");
        var existingSignal = GitHubPullRequestSignal.Create(openPr, ["wi-3"]);

        var closedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps");

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([closedPr]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-3", Signals = [existingSignal] }]);

        var collector = new TestableGitHubCollector(CreatePrConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.True(prSignal.Info.Merged);
        Assert.Equal(["wi-3"], prSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_MixedIssuesAndPrs_CollectsBoth()
    {
        var issue = CreateIssue(1, ItemState.Open, "Bug");
        var pr = CreatePullRequest(10, ItemState.Open, title: "Update dependencies");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);
        client.PullRequest.GetAllForRepository("dotnet", "runtime", Arg.Any<PullRequestRequest>())
            .Returns([pr]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(Arg.Any<Enum>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateMixedConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s is GitHubIssueSignal);
        Assert.Contains(signals, s => s is GitHubPullRequestSignal);
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_SameState_UpdatedSince_CollectsSignal()
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var originalIssue = CreateIssue(1, ItemState.Open, updatedAt: originalTime);
        var existingSignal = GitHubIssueSignal.Create(originalIssue, ["wi-1"]);

        var updatedIssue = CreateIssue(1, ItemState.Open, updatedAt: updatedTime);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([updatedIssue]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-1", Signals = [existingSignal] }]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([]);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        // Issue was updated (e.g. new comment) even though state is still Open — should be collected
        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(updatedTime, issueSignal.Info.UpdatedAt);
        Assert.Equal(["wi-1"], issueSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_UpdatedSince_CollectsSignal()
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: originalTime);
        var existingSignal = GitHubPullRequestSignal.Create(originalPr, ["wi-5"]);

        var updatedPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: updatedTime);

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([updatedPr]);

        var workItemsProvider = Substitute.For<IWorkItemsProvider>();
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, Arg.Any<CancellationToken>())
            .Returns([]);
        workItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, Arg.Any<CancellationToken>())
            .Returns([new WorkItem { Id = "wi-5", Signals = [existingSignal] }]);

        var collector = new TestableGitHubCollector(CreatePrConfig(), workItemsProvider, client);
        var signals = await collector.CollectAsync();

        // PR was updated (e.g. new review comment) even though state is still Open — should be collected
        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(updatedTime, prSignal.Info.UpdatedAt);
        Assert.Equal(["wi-5"], prSignal.WorkItemIds);
    }

    [Fact]
    public void MatchesAnyPattern_MatchesCorrectly()
    {
        var patterns = new[] { new Regex("^Fix"), new Regex("^Update") };
        Assert.True(GitHubSignalCollector.MatchesAnyPattern("Update deps", patterns));
        Assert.True(GitHubSignalCollector.MatchesAnyPattern("Fix bug", patterns));
        Assert.False(GitHubSignalCollector.MatchesAnyPattern("Add feature", patterns));
    }
}
