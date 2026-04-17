using System.Text.RegularExpressions;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using NSubstitute;
using Octokit;
using Xunit;

namespace BuildDuty.Tests;

public class GitHubSignalTests
{
    private static readonly User TestUser = new();

    private static Issue CreateIssue(int number, ItemState state, string title = "Test issue", bool isPr = false, DateTimeOffset? updatedAt = null, User? user = null, IReadOnlyList<Label>? labels = null, string org = "dotnet", string repo = "runtime") => new(
        url: $"https://api.github.com/repos/{org}/{repo}/issues/{number}",
        htmlUrl: isPr ? $"https://github.com/{org}/{repo}/pull/{number}" : $"https://github.com/{org}/{repo}/issues/{number}",
        commentsUrl: "", eventsUrl: "",
        number: number, state: state, title: title, body: "",
        closedBy: null, user: user ?? TestUser, labels: labels ?? [], assignee: null, assignees: [],
        milestone: null, comments: 0,
        pullRequest: isPr ? new PullRequest() : null,
        closedAt: null, createdAt: DateTimeOffset.Now, updatedAt: updatedAt,
        id: number, nodeId: "", locked: false, repository: null,
        reactions: null, stateReason: null, activeLockReason: null);

    private static PullRequest CreatePullRequest(int number, ItemState state, bool merged = false, string title = "Test PR", DateTimeOffset? updatedAt = null, User? user = null, IReadOnlyList<Label>? labels = null) => new(
        id: number, nodeId: "",
        url: $"https://api.github.com/repos/dotnet/sdk/pulls/{number}",
        htmlUrl: $"https://github.com/dotnet/sdk/pull/{number}",
        diffUrl: "", patchUrl: "", issueUrl: "", statusesUrl: "",
        number: number, state: state, title: title, body: "",
        createdAt: DateTimeOffset.Now, updatedAt: updatedAt ?? DateTimeOffset.Now,
        closedAt: null, mergedAt: merged ? DateTimeOffset.Now : null,
        head: new GitReference("", "", "", "", "abc123", TestUser, null), @base: null, user: user ?? TestUser,
        assignee: null, assignees: [], draft: false,
        mergeable: null, mergeableState: null, mergedBy: null,
        mergeCommitSha: "", comments: 0, commits: 0,
        additions: 0, deletions: 0, changedFiles: 0,
        milestone: null, locked: false, maintainerCanModify: null,
        requestedReviewers: [], requestedTeams: [], labels: labels ?? [],
        activeLockReason: null);

    private static GitHubIssueInfo ToIssueInfo(Issue issue) => new(
        new GitHubItemInfo(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt, null, null));

    private static GitHubPullRequestInfo ToPrInfo(PullRequest pr) => new(
        new GitHubItemInfo(pr.Number, pr.Title, pr.State.Value.ToString(), pr.UpdatedAt, null, null), pr.Merged, null);

    private static GitHubConfig CreateIssueConfig(string org = "dotnet", string repo = "runtime", List<string>? labels = null, List<string>? authors = null, List<string>? excludeLabels = null) => new()
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
                        Issues = [new GitHubItemConfig
                        {
                            Labels = labels ?? [],
                            State = ItemStateFilter.Open,
                            Authors = authors ?? [],
                            ExcludeLabels = excludeLabels ?? [],
                        }]
                    }
                ]
            }
        ]
    };

    private static GitHubConfig CreatePrConfig(string org = "dotnet", string repo = "sdk", List<GitHubItemConfig>? configs = null) => new()
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
                        PullRequests = configs ?? [new GitHubItemConfig { Name = new Regex(".*"), State = ItemStateFilter.Open }]
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
                        Issues = [new GitHubItemConfig { Labels = ["bug"], State = ItemStateFilter.Open }],
                        PullRequests = [new GitHubItemConfig { Name = new Regex("^Update"), State = ItemStateFilter.Open }]
                    }
                ]
            }
        ]
    };

    private static void SetupPullRequestMocks(IGitHubClient client)
    {
        client.Issue.Comment.GetAllForIssue(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>())
            .Returns(Array.Empty<IssueComment>());
        client.Check.Run.GetAllForReference(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new CheckRunsResponse(0, Array.Empty<CheckRun>()));
    }

    private class TestableGitHubCollector : GitHubSignalCollector
    {
        private readonly IGitHubClient _client;

        public TestableGitHubCollector(
            GitHubConfig config,
            IStorageProvider storageProvider,
            IGitHubClient client)
            : base(config, Substitute.For<IGeneralTokenProvider>(), storageProvider)
        {
            _client = client;
        }

        protected override Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
        {
            return Task.FromResult(new RepositoryContext(organization, repository, _client));
        }
    }

    [Fact]
    public async Task CollectAsync_NewIssues_NoExistingWorkItems_CreatesSignals()
    {
        var issue1 = CreateIssue(1, ItemState.Open, "Bug A", labels: [CreateLabel("bug")]);
        var issue2 = CreateIssue(2, ItemState.Open, "Bug B", labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue1, issue2]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var savedSignals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        Assert.Equal(2, savedSignals.Count);
        Assert.All(savedSignals, s =>
        {
            var issueSignal = Assert.IsType<GitHubIssueSignal>(s);
        });
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_SameState_SkipsSignal()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("bug")]);
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(issue), new Uri(issue.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_DifferentState_Collects()
    {
        var openIssue = CreateIssue(1, ItemState.Open, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), labels: [CreateLabel("bug")]);
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(openIssue), new Uri(openIssue.HtmlUrl));

        var closedIssue = CreateIssue(1, ItemState.Closed, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([closedIssue]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal("Closed", issueSignal.TypedInfo.ItemInfo.State);
    }

    [Fact]
    public async Task CollectAsync_IssueConfig_OnlyCollectsActualIssues()
    {
        var realIssue = CreateIssue(1, ItemState.Open, "Real issue", labels: [CreateLabel("bug")]);
        var prAsIssue = CreateIssue(2, ItemState.Open, "Actually a PR", isPr: true, labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([realIssue, prAsIssue]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        Assert.Single(signals);
        Assert.All(signals, s => Assert.IsType<GitHubIssueSignal>(s));
    }

    [Fact]
    public async Task CollectAsync_PullRequests_OnlyMatchingPatternsCollected()
    {
        var matchIssue = CreateIssue(1, ItemState.Open, title: "Update dependencies", isPr: true, org: "dotnet", repo: "sdk");
        var noMatchIssue = CreateIssue(2, ItemState.Open, title: "Fix something", isPr: true, org: "dotnet", repo: "sdk");
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update dependencies");

        var configs = new List<GitHubItemConfig>
        {
            new() { Name = new Regex("^Update"), State = ItemStateFilter.Open }
        };

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, noMatchIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(matchPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(configs: configs), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal("Update dependencies", prSignal.TypedInfo.ItemInfo.Title);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_SkipsSignal()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: time);
        var prAsIssue = CreateIssue(1, ItemState.Open, title: "Update deps", isPr: true, updatedAt: time, org: "dotnet", repo: "sdk");
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(pr), new Uri(pr.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([prAsIssue]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-3", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(), storageProvider, client);
        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_DifferentState_CollectsWithPreservedWorkItemIds()
    {
        var openPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(openPr), new Uri(openPr.HtmlUrl));

        var closedIssue = CreateIssue(1, ItemState.Closed, title: "Update deps", isPr: true, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), org: "dotnet", repo: "sdk");
        var closedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([closedIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(closedPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-3", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.True(prSignal.TypedInfo.Merged);
    }

    [Fact]
    public async Task CollectAsync_MixedIssuesAndPrs_CollectsBoth()
    {
        var issue = CreateIssue(1, ItemState.Open, "Bug", labels: [CreateLabel("bug")]);
        var prAsIssue = CreateIssue(10, ItemState.Open, title: "Update dependencies", isPr: true);
        var fullPr = CreatePullRequest(10, ItemState.Open, title: "Update dependencies");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue, prAsIssue]);
        client.PullRequest.Get("dotnet", "runtime", 10).Returns(fullPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateMixedConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        Assert.Equal(2, signals.Count);
        Assert.Contains(signals, s => s is GitHubIssueSignal);
        Assert.Contains(signals, s => s is GitHubPullRequestSignal);
    }

    [Fact]
    public async Task CollectAsync_ExistingIssue_SameState_UpdatedSince_CollectsSignal()
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var originalIssue = CreateIssue(1, ItemState.Open, updatedAt: originalTime, labels: [CreateLabel("bug")]);
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(originalIssue), new Uri(originalIssue.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Open, updatedAt: updatedTime, labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([updatedIssue]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        // Issue was updated (e.g. new comment) even though state is still Open — should be collected
        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(updatedTime, issueSignal.TypedInfo.ItemInfo.UpdatedAt);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_UpdatedSince_CollectsSignal()
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: originalTime);
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(originalPr), new Uri(originalPr.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Open, title: "Update deps", isPr: true, updatedAt: updatedTime, org: "dotnet", repo: "sdk");
        var updatedPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: updatedTime);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([updatedIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(updatedPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-5", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        // PR was updated (e.g. new review comment) even though state is still Open — should be collected
        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(updatedTime, prSignal.TypedInfo.ItemInfo.UpdatedAt);
    }

    [Fact]
    public void MatchesAnyPattern_MatchesCorrectly()
    {
        var patterns = new[] { new Regex("^Fix"), new Regex("^Update") };
        Assert.True(GitHubSignalCollector.MatchesAnyPattern("Update deps", patterns));
        Assert.True(GitHubSignalCollector.MatchesAnyPattern("Fix bug", patterns));
        Assert.False(GitHubSignalCollector.MatchesAnyPattern("Add feature", patterns));
    }

    [Fact]
    public async Task CollectAsync_NoIssueConfig_ExistingIssueUpdated_RefetchesSignal()
    {
        var originalIssue = CreateIssue(1, ItemState.Open, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(originalIssue), new Uri(originalIssue.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Closed, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("dotnet", "runtime", 1).Returns(updatedIssue);

        // Config with no Issues set (null)
        var config = new GitHubConfig()
        {
            Organizations =
            [
                new GitHubOrganizationConfig
                {
                    Organization = "dotnet",
                    Repositories = [new GitHubRepositoryConfig { Name = "runtime" }]
                }
            ]
        };

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(config, storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(existingSignal.Id, issueSignal.Id);
        Assert.Equal("Closed", issueSignal.TypedInfo.ItemInfo.State);
    }

    [Fact]
    public async Task CollectAsync_NoIssueConfig_ExistingIssueUnchanged_SkipsSignal()
    {
        var issue = CreateIssue(1, ItemState.Open);
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(issue), new Uri(issue.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("dotnet", "runtime", 1).Returns(issue);

        var config = new GitHubConfig
        {
            Organizations =
            [
                new GitHubOrganizationConfig
                {
                    Organization = "dotnet",
                    Repositories = [new GitHubRepositoryConfig { Name = "runtime" }]
                }
            ]
        };

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(config, storageProvider, client);
        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_NoPrConfig_ExistingPrUpdated_RefetchesSignal()
    {
        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(originalPr), new Uri(originalPr.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Closed, title: "Update deps", isPr: true, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), org: "dotnet", repo: "sdk");
        var mergedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("dotnet", "sdk", 1).Returns(updatedIssue);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(mergedPr);
        SetupPullRequestMocks(client);

        // Config with no PullRequests set (null)
        var config = new GitHubConfig
        {
            Organizations =
            [
                new GitHubOrganizationConfig
                {
                    Organization = "dotnet",
                    Repositories = [new GitHubRepositoryConfig { Name = "sdk" }]
                }
            ]
        };

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-3", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(config, storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(existingSignal.Id, prSignal.Id);
        Assert.True(prSignal.TypedInfo.Merged);
    }

    [Fact]
    public async Task CollectAsync_NoPrConfig_ExistingPrUnchanged_SkipsSignal()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: time);
        var prAsIssue = CreateIssue(1, ItemState.Open, title: "Update deps", isPr: true, updatedAt: time, org: "dotnet", repo: "sdk");
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(pr), new Uri(pr.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.Issue.Get("dotnet", "sdk", 1).Returns(prAsIssue);

        var config = new GitHubConfig
        {
            Organizations =
            [
                new GitHubOrganizationConfig
                {
                    Organization = "dotnet",
                    Repositories = [new GitHubRepositoryConfig { Name = "sdk" }]
                }
            ]
        };

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-3", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(config, storageProvider, client);
        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_IssueFellOutOfQuery_RefetchesUpdatedSignal()
    {
        // Issue was previously open+labeled, now closed (no longer in query results)
        var originalIssue = CreateIssue(1, ItemState.Open, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(originalIssue), new Uri(originalIssue.HtmlUrl));

        var closedIssue = CreateIssue(1, ItemState.Closed, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        // Query returns no issues (the issue fell out)
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns(Array.Empty<Issue>());
        // But individual fetch returns the closed issue
        client.Issue.Get("dotnet", "runtime", 1).Returns(closedIssue);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(existingSignal.Id, issueSignal.Id);
        Assert.Equal("Closed", issueSignal.TypedInfo.ItemInfo.State);
    }

    [Fact]
    public async Task CollectAsync_PrFellOutOfQuery_RefetchesUpdatedSignal()
    {
        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(originalPr), new Uri(originalPr.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Closed, title: "Update deps", isPr: true, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), org: "dotnet", repo: "sdk");
        var mergedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        // Query returns no items (the PR fell out)
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns(Array.Empty<Issue>());
        // But individual fetch returns the closed issue (which is a PR)
        client.Issue.Get("dotnet", "sdk", 1).Returns(updatedIssue);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(mergedPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-3", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(existingSignal.Id, prSignal.Id);
        Assert.True(prSignal.TypedInfo.Merged);
    }

    private static User CreateUser(string login)
    {
        var user = new User();
        var setter = (typeof(Account).GetProperty("Login") ?? typeof(User).GetProperty("Login"))
            ?.GetSetMethod(nonPublic: true);
        setter!.Invoke(user, [login]);
        return user;
    }

    private static Label CreateLabel(string name) => new(0, "", name, "", "", "", false);

    private static TimelineEventInfo CreateCrossRefEvent(Issue sourceIssue)
    {
        // Extract org/repo from the issue's API URL (e.g., https://api.github.com/repos/dotnet/sdk/issues/50)
        var uri = new Uri(sourceIssue.Url);
        var segments = uri.AbsolutePath.Split('/');
        // /repos/{org}/{repo}/issues/{number} → segments: "", "repos", org, repo, "issues", number
        var sourceUrl = $"https://api.github.com/repos/{segments[2]}/{segments[3]}";

        return new(id: 0, nodeId: "", url: "", actor: null!, commitId: "",
            @event: EventInfoState.Crossreferenced, createdAt: DateTimeOffset.Now,
            label: null!, assignee: null!, milestone: null!,
            source: new SourceInfo(null!, 0, sourceIssue, sourceUrl),
            rename: null!, projectCard: null!);
    }

    private static void SetupTimelineMock(IGitHubClient client, string org, string repo, long issueNumber, params TimelineEventInfo[] events)
    {
        client.Issue.Timeline.GetAllForIssue(org, repo, issueNumber)
            .Returns(events.ToList());
    }

    private static void SetupEmptyTimeline(IGitHubClient client)
    {
        client.Issue.Timeline.GetAllForIssue(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>())
            .Returns(Array.Empty<TimelineEventInfo>());
    }

    [Fact]
    public void MatchesItemConfig_NoFilters_MatchesOnTitle()
    {
        var issue = CreateIssue(1, ItemState.Open, title: "Update dependencies");
        var config = new GitHubItemConfig { Name = new Regex("^Update") };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
        Assert.False(GitHubSignalCollector.MatchesItemConfig(
            CreateIssue(2, ItemState.Open, title: "Fix bug"), config));
    }

    [Fact]
    public void MatchesItemConfig_AuthorFilter_MatchingAuthor_Matches()
    {
        var user = CreateUser("dotnet-bot");
        var issue = CreateIssue(1, ItemState.Open, title: "Update deps", user: user);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            Authors = ["dotnet-bot", "dependabot"]
        };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_AuthorFilter_NonMatchingAuthor_DoesNotMatch()
    {
        var user = CreateUser("some-other-user");
        var issue = CreateIssue(1, ItemState.Open, title: "Update deps", user: user);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            Authors = ["dotnet-bot", "dependabot"]
        };

        Assert.False(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_AppAuthorFilter_MatchesBotLogin()
    {
        var user = CreateUser("dotnet-maestro[bot]");
        var issue = CreateIssue(1, ItemState.Open, title: "Update deps", user: user);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            Authors = ["app/dotnet-maestro"]
        };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_LabelFilter_MatchingLabel_Matches()
    {
        var issue = CreateIssue(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("bug"), CreateLabel("p1")]);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            Labels = ["bug"]
        };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_LabelFilter_NoMatchingLabel_DoesNotMatch()
    {
        var issue = CreateIssue(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("enhancement")]);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            Labels = ["bug"]
        };

        Assert.False(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_ExcludeLabelFilter_ExcludedLabel_DoesNotMatch()
    {
        var issue = CreateIssue(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("backport")]);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            ExcludeLabels = ["backport", "DO NOT MERGE"]
        };

        Assert.False(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_ExcludeLabelFilter_NoExcludedLabel_Matches()
    {
        var issue = CreateIssue(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("bug")]);
        var config = new GitHubItemConfig
        {
            Name = new Regex(".*"),
            ExcludeLabels = ["backport", "DO NOT MERGE"]
        };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByAuthor_OnlyMatchingAuthorCollected()
    {
        var authorUser = CreateUser("dotnet-bot");
        var otherUser = CreateUser("other-user");

        var matchIssue = CreateIssue(1, ItemState.Open, title: "Update deps", isPr: true, user: authorUser, org: "dotnet", repo: "sdk");
        var noMatchIssue = CreateIssue(2, ItemState.Open, title: "Update deps", isPr: true, user: otherUser, org: "dotnet", repo: "sdk");
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", user: authorUser);

        var configs = new List<GitHubItemConfig>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, Authors = ["dotnet-bot"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, noMatchIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(matchPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(configs: configs), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.ItemInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByLabel_OnlyMatchingLabelCollected()
    {
        var matchIssue = CreateIssue(1, ItemState.Open, title: "Fix crash", isPr: true, labels: [CreateLabel("bug")], org: "dotnet", repo: "sdk");
        var noMatchIssue = CreateIssue(2, ItemState.Open, title: "Add feature", isPr: true, labels: [CreateLabel("enhancement")], org: "dotnet", repo: "sdk");
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("bug")]);

        var configs = new List<GitHubItemConfig>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, Labels = ["bug"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, noMatchIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(matchPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(configs: configs), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.ItemInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByExcludeLabel_ExcludedLabelNotCollected()
    {
        var matchIssue = CreateIssue(1, ItemState.Open, title: "Update deps", isPr: true, labels: [CreateLabel("bug")], org: "dotnet", repo: "sdk");
        var excludedIssue = CreateIssue(2, ItemState.Open, title: "Update deps", isPr: true, labels: [CreateLabel("backport")], org: "dotnet", repo: "sdk");
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("bug")]);

        var configs = new List<GitHubItemConfig>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, ExcludeLabels = ["backport"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "sdk", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, excludedIssue]);
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(matchPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(configs: configs), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.ItemInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_MixedConfig_PrConfigDoesNotDoubleCollectIssues()
    {
        // Regression test: a broad PR config (Name = ".*") should NOT collect regular issues,
        // even though the GitHub Issues API returns both issues and PRs in the same response.
        var realIssue = CreateIssue(1, ItemState.Open, "Bug report", labels: [CreateLabel("bug")]);
        var prAsIssue = CreateIssue(10, ItemState.Open, title: "Bug fix PR", isPr: true);
        var fullPr = CreatePullRequest(10, ItemState.Open, title: "Bug fix PR");

        var config = new GitHubConfig
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
                            Issues = [new GitHubItemConfig { Labels = ["bug"], State = ItemStateFilter.Open }],
                            // Broad PR config that matches everything — previously would also collect the issue
                            PullRequests = [new GitHubItemConfig { Name = new Regex(".*"), State = ItemStateFilter.Open }]
                        }
                    ]
                }
            ]
        };

        var client = Substitute.For<IGitHubClient>();
        // The GitHub Issues API returns BOTH the real issue and the PR
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([realIssue, prAsIssue]);
        client.PullRequest.Get("dotnet", "runtime", 10).Returns(fullPr);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(config, storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        // Should be exactly 2 signals: one issue, one PR — NOT 3 (which would happen if
        // the PR config also collected the regular issue as a GitHubPullRequestSignal)
        Assert.Equal(2, signals.Count);
        Assert.Single(signals.OfType<GitHubIssueSignal>());
        Assert.Single(signals.OfType<GitHubPullRequestSignal>());

        var issueSignal = signals.OfType<GitHubIssueSignal>().Single();
        Assert.Equal(1, issueSignal.TypedInfo.ItemInfo.Number);
        Assert.Equal("Bug report", issueSignal.TypedInfo.ItemInfo.Title);

        var prSignal = signals.OfType<GitHubPullRequestSignal>().Single();
        Assert.Equal(10, prSignal.TypedInfo.ItemInfo.Number);
        Assert.Equal("Bug fix PR", prSignal.TypedInfo.ItemInfo.Title);
    }

    [Fact]
    public void MatchesItemConfig_NoFilters_AlwaysMatches()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("bug"), CreateLabel("priority-1")]);
        var prAsIssue = CreateIssue(2, ItemState.Open, isPr: true, labels: [CreateLabel("enhancement"), CreateLabel("area-build")]);
        var config = new GitHubItemConfig();

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
        Assert.True(GitHubSignalCollector.MatchesItemConfig(prAsIssue, config));
    }

    [Fact]
    public async Task CollectAsync_NoLabelFilter_CollectsOnlyIssuesFromIssueConfig()
    {
        var issue1 = CreateIssue(1, ItemState.Open, "Bug with labels", labels: [CreateLabel("bug"), CreateLabel("priority-1")]);
        var issue2 = CreateIssue(2, ItemState.Open, "Feature request", labels: [CreateLabel("enhancement"), CreateLabel("area-build")]);
        var prAsIssue = CreateIssue(3, ItemState.Open, "Update deps", isPr: true, labels: [CreateLabel("dependencies"), CreateLabel("security")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue1, issue2, prAsIssue]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        Assert.Equal(2, signals.Count);
        Assert.All(signals, s => Assert.IsType<GitHubIssueSignal>(s));
    }

    [Fact]
    public void MatchesItemConfig_AuthorFilter_MatchingIssueAuthor_Matches()
    {
        var user = CreateUser("dotnet-bot");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubItemConfig { Authors = ["dotnet-bot", "dependabot"] };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_AuthorFilter_NonMatchingIssueAuthor_DoesNotMatch()
    {
        var user = CreateUser("some-other-user");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubItemConfig { Authors = ["dotnet-bot", "dependabot"] };

        Assert.False(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_AppAuthorFilter_MatchesIssueBotLogin()
    {
        var user = CreateUser("dotnet-maestro[bot]");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubItemConfig { Authors = ["app/dotnet-maestro"] };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_ExcludeLabelFilter_ExcludedIssueLabel_DoesNotMatch()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("wontfix")]);
        var config = new GitHubItemConfig { ExcludeLabels = ["wontfix", "duplicate"] };

        Assert.False(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public void MatchesItemConfig_ExcludeLabelFilter_NoExcludedIssueLabel_Matches()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("bug")]);
        var config = new GitHubItemConfig { ExcludeLabels = ["wontfix", "duplicate"] };

        Assert.True(GitHubSignalCollector.MatchesItemConfig(issue, config));
    }

    [Fact]
    public async Task CollectAsync_Issues_FilteredByAuthor_OnlyMatchingAuthorCollected()
    {
        var authorUser = CreateUser("dotnet-bot");
        var otherUser = CreateUser("other-user");

        var matchIssue = CreateIssue(1, ItemState.Open, title: "Bug A", user: authorUser, labels: [CreateLabel("bug")]);
        var noMatchIssue = CreateIssue(2, ItemState.Open, title: "Bug B", user: otherUser, labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, noMatchIssue]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(authors: ["dotnet-bot"]), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(1, issueSignal.TypedInfo.ItemInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_Issues_FilteredByExcludeLabel_ExcludedLabelNotCollected()
    {
        var matchIssue = CreateIssue(1, ItemState.Open, title: "Bug A", labels: [CreateLabel("bug")]);
        var excludedIssue = CreateIssue(2, ItemState.Open, title: "Bug B", labels: [CreateLabel("wontfix")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([matchIssue, excludedIssue]);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(excludeLabels: ["wontfix"]), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.Equal(1, issueSignal.TypedInfo.ItemInfo.Number);
    }

    #region Timeline event tests

    [Fact]
    public async Task CollectAsync_IssueWithCrossReferencedPr_IncludesTimelineEvents()
    {
        var issue = CreateIssue(1, ItemState.Open, "Bug A", labels: [CreateLabel("bug")]);
        var linkedPrIssue = CreateIssue(50, ItemState.Open, "Fix bug A", isPr: true, org: "dotnet", repo: "sdk");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);
        client.Issue.Comment.GetAllForIssue("dotnet", "runtime", 1)
            .Returns(Array.Empty<IssueComment>());
        SetupEmptyTimeline(client);
        SetupTimelineMock(client, "dotnet", "runtime", 1, CreateCrossRefEvent(linkedPrIssue));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.NotNull(issueSignal.TypedInfo.TimelineEvents);
        var evt = Assert.Single(issueSignal.TypedInfo.TimelineEvents);
        Assert.Equal("cross-referenced", evt.Event);
        Assert.Equal("https://github.com/dotnet/sdk/pull/50", evt.SourceUrl);
        Assert.Equal("Open", evt.SourceState);
    }

    [Fact]
    public async Task CollectAsync_IssueWithCrossReferencedIssue_ExcludesNonPrEvents()
    {
        var issue = CreateIssue(1, ItemState.Open, "Bug A", labels: [CreateLabel("bug")]);
        var crossRefIssue = CreateIssue(2, ItemState.Open, "Related", isPr: false, org: "dotnet", repo: "runtime");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);
        client.Issue.Comment.GetAllForIssue("dotnet", "runtime", 1)
            .Returns(Array.Empty<IssueComment>());
        SetupEmptyTimeline(client);
        SetupTimelineMock(client, "dotnet", "runtime", 1, CreateCrossRefEvent(crossRefIssue));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        // Cross-referenced issues (not PRs) should be excluded
        Assert.NotNull(issueSignal.TypedInfo.TimelineEvents);
        Assert.Empty(issueSignal.TypedInfo.TimelineEvents);
    }

    [Fact]
    public async Task CollectAsync_IssueWithNoTimelineEvents_HasEmptyList()
    {
        var issue = CreateIssue(1, ItemState.Open, "Bug A", labels: [CreateLabel("bug")]);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);
        client.Issue.Comment.GetAllForIssue("dotnet", "runtime", 1)
            .Returns(Array.Empty<IssueComment>());
        SetupEmptyTimeline(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreateIssueConfig(), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var issueSignal = Assert.IsType<GitHubIssueSignal>(signal);
        Assert.NotNull(issueSignal.TypedInfo.TimelineEvents);
        Assert.Empty(issueSignal.TypedInfo.TimelineEvents);
    }

    #endregion
}
