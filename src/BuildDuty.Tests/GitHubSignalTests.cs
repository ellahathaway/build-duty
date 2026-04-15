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

    private static Issue CreateIssue(int number, ItemState state, string title = "Test issue", bool isPr = false, DateTimeOffset? updatedAt = null, User? user = null, IReadOnlyList<Label>? labels = null) => new(
        url: $"https://api.github.com/repos/dotnet/runtime/issues/{number}",
        htmlUrl: $"https://github.com/dotnet/runtime/issues/{number}",
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
        issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt, null, null);

    private static GitHubPullRequestInfo ToPrInfo(PullRequest pr) => new(
        pr.Number, pr.Title, pr.State.Value.ToString(), pr.UpdatedAt, pr.Merged, null, null, null);

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
                        Issues = new GitHubIssueConfig
                        {
                            Labels = labels ?? ["bug"],
                            State = ItemStateFilter.Open,
                            Authors = authors ?? [],
                            ExcludeLabels = excludeLabels ?? [],
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
        var issue1 = CreateIssue(1, ItemState.Open, "Bug A");
        var issue2 = CreateIssue(2, ItemState.Open, "Bug B");

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
        var issue = CreateIssue(1, ItemState.Open);
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
        var openIssue = CreateIssue(1, ItemState.Open, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(openIssue), new Uri(openIssue.HtmlUrl));

        var closedIssue = CreateIssue(1, ItemState.Closed, updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

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
        Assert.Equal("Closed", issueSignal.TypedInfo.State);
    }

    [Fact]
    public async Task CollectAsync_IssuesThatArePullRequests_AreFiltered()
    {
        var realIssue = CreateIssue(1, ItemState.Open, "Real issue");
        var prAsIssue = CreateIssue(2, ItemState.Open, "Actually a PR", isPr: true);

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([realIssue, prAsIssue]);

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
        Assert.Equal("Real issue", issueSignal.TypedInfo.Title);
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
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(patterns: patterns), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal("Update dependencies", prSignal.TypedInfo.Title);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_SkipsSignal()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps");
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(pr), new Uri(pr.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([pr]);
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

        var closedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([closedPr]);
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
        var issue = CreateIssue(1, ItemState.Open, "Bug");
        var pr = CreatePullRequest(10, ItemState.Open, title: "Update dependencies");

        var client = Substitute.For<IGitHubClient>();
        client.Issue.GetAllForRepository("dotnet", "runtime", Arg.Any<RepositoryIssueRequest>())
            .Returns([issue]);
        client.PullRequest.GetAllForRepository("dotnet", "runtime", Arg.Any<PullRequestRequest>())
            .Returns([pr]);
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

        var originalIssue = CreateIssue(1, ItemState.Open, updatedAt: originalTime);
        var existingSignal = new GitHubIssueSignal(ToIssueInfo(originalIssue), new Uri(originalIssue.HtmlUrl));

        var updatedIssue = CreateIssue(1, ItemState.Open, updatedAt: updatedTime);

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
        Assert.Equal(updatedTime, issueSignal.TypedInfo.UpdatedAt);
    }

    [Fact]
    public async Task CollectAsync_ExistingPr_SameState_UpdatedSince_CollectsSignal()
    {
        var originalTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedTime = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: originalTime);
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(originalPr), new Uri(originalPr.HtmlUrl));

        var updatedPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: updatedTime);

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([updatedPr]);
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
        Assert.Equal(updatedTime, prSignal.TypedInfo.UpdatedAt);
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
        Assert.Equal("Closed", issueSignal.TypedInfo.State);
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

        var mergedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
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
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps");
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(pr), new Uri(pr.HtmlUrl));

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Get("dotnet", "sdk", 1).Returns(pr);

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
        Assert.Equal("Closed", issueSignal.TypedInfo.State);
    }

    [Fact]
    public async Task CollectAsync_PrFellOutOfQuery_RefetchesUpdatedSignal()
    {
        var originalPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var existingSignal = new GitHubPullRequestSignal(ToPrInfo(originalPr), new Uri(originalPr.HtmlUrl));

        var mergedPr = CreatePullRequest(1, ItemState.Closed, merged: true, title: "Update deps", updatedAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var client = Substitute.For<IGitHubClient>();
        // Query returns no PRs (the PR fell out)
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns(Array.Empty<PullRequest>());
        // But individual fetch returns the merged PR
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

    private static Label CreateLabel(string name) => new(0, "", name, "", "", false, "");

    [Fact]
    public void MatchesPullRequestPattern_NoFilters_MatchesOnTitle()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update dependencies");
        var pattern = new GitHubPullRequestPattern { Name = new Regex("^Update") };

        Assert.True(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
        Assert.False(GitHubSignalCollector.MatchesPullRequestPattern(
            CreatePullRequest(2, ItemState.Open, title: "Fix bug"), pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_AuthorFilter_MatchingAuthor_Matches()
    {
        var user = CreateUser("dotnet-bot");
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", user: user);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            Authors = ["dotnet-bot", "dependabot"]
        };

        Assert.True(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_AuthorFilter_NonMatchingAuthor_DoesNotMatch()
    {
        var user = CreateUser("some-other-user");
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", user: user);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            Authors = ["dotnet-bot", "dependabot"]
        };

        Assert.False(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_AppAuthorFilter_MatchesBotLogin()
    {
        var user = CreateUser("dotnet-maestro[bot]");
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", user: user);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            Authors = ["app/dotnet-maestro"]
        };

        Assert.True(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_LabelFilter_MatchingLabel_Matches()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("bug"), CreateLabel("p1")]);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            Labels = ["bug"]
        };

        Assert.True(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_LabelFilter_NoMatchingLabel_DoesNotMatch()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("enhancement")]);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            Labels = ["bug"]
        };

        Assert.False(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_ExcludeLabelFilter_ExcludedLabel_DoesNotMatch()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("backport")]);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            ExcludeLabels = ["backport", "DO NOT MERGE"]
        };

        Assert.False(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public void MatchesPullRequestPattern_ExcludeLabelFilter_NoExcludedLabel_Matches()
    {
        var pr = CreatePullRequest(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("bug")]);
        var pattern = new GitHubPullRequestPattern
        {
            Name = new Regex(".*"),
            ExcludeLabels = ["backport", "DO NOT MERGE"]
        };

        Assert.True(GitHubSignalCollector.MatchesPullRequestPattern(pr, pattern));
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByAuthor_OnlyMatchingAuthorCollected()
    {
        var authorUser = CreateUser("dotnet-bot");
        var otherUser = CreateUser("other-user");

        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", user: authorUser);
        var noMatchPr = CreatePullRequest(2, ItemState.Open, title: "Update deps", user: otherUser);

        var patterns = new List<GitHubPullRequestPattern>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, Authors = ["dotnet-bot"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([matchPr, noMatchPr]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(patterns: patterns), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByLabel_OnlyMatchingLabelCollected()
    {
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Fix crash", labels: [CreateLabel("bug")]);
        var noMatchPr = CreatePullRequest(2, ItemState.Open, title: "Add feature", labels: [CreateLabel("enhancement")]);

        var patterns = new List<GitHubPullRequestPattern>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, Labels = ["bug"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([matchPr, noMatchPr]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(patterns: patterns), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.Number);
    }

    [Fact]
    public async Task CollectAsync_PullRequests_FilteredByExcludeLabel_ExcludedLabelNotCollected()
    {
        var matchPr = CreatePullRequest(1, ItemState.Open, title: "Update deps", labels: [CreateLabel("bug")]);
        var excludedPr = CreatePullRequest(2, ItemState.Open, title: "Update deps", labels: [CreateLabel("backport")]);

        var patterns = new List<GitHubPullRequestPattern>
        {
            new() { Name = new Regex(".*"), State = ItemStateFilter.Open, ExcludeLabels = ["backport"] }
        };

        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.GetAllForRepository("dotnet", "sdk", Arg.Any<PullRequestRequest>())
            .Returns([matchPr, excludedPr]);
        SetupPullRequestMocks(client);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var collector = new TestableGitHubCollector(CreatePrConfig(patterns: patterns), storageProvider, client);
        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var prSignal = Assert.IsType<GitHubPullRequestSignal>(signal);
        Assert.Equal(1, prSignal.TypedInfo.Number);
    }

    [Fact]
    public void MatchesIssueConfig_NoFilters_AlwaysMatches()
    {
        var issue = CreateIssue(1, ItemState.Open);
        var config = new GitHubIssueConfig();

        Assert.True(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public void MatchesIssueConfig_AuthorFilter_MatchingAuthor_Matches()
    {
        var user = CreateUser("dotnet-bot");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubIssueConfig { Authors = ["dotnet-bot", "dependabot"] };

        Assert.True(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public void MatchesIssueConfig_AuthorFilter_NonMatchingAuthor_DoesNotMatch()
    {
        var user = CreateUser("some-other-user");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubIssueConfig { Authors = ["dotnet-bot", "dependabot"] };

        Assert.False(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public void MatchesIssueConfig_AppAuthorFilter_MatchesBotLogin()
    {
        var user = CreateUser("dotnet-maestro[bot]");
        var issue = CreateIssue(1, ItemState.Open, user: user);
        var config = new GitHubIssueConfig { Authors = ["app/dotnet-maestro"] };

        Assert.True(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public void MatchesIssueConfig_ExcludeLabelFilter_ExcludedLabel_DoesNotMatch()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("wontfix")]);
        var config = new GitHubIssueConfig { ExcludeLabels = ["wontfix", "duplicate"] };

        Assert.False(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public void MatchesIssueConfig_ExcludeLabelFilter_NoExcludedLabel_Matches()
    {
        var issue = CreateIssue(1, ItemState.Open, labels: [CreateLabel("bug")]);
        var config = new GitHubIssueConfig { ExcludeLabels = ["wontfix", "duplicate"] };

        Assert.True(GitHubSignalCollector.MatchesIssueConfig(issue, config));
    }

    [Fact]
    public async Task CollectAsync_Issues_FilteredByAuthor_OnlyMatchingAuthorCollected()
    {
        var authorUser = CreateUser("dotnet-bot");
        var otherUser = CreateUser("other-user");

        var matchIssue = CreateIssue(1, ItemState.Open, title: "Bug A", user: authorUser);
        var noMatchIssue = CreateIssue(2, ItemState.Open, title: "Bug B", user: otherUser);

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
        Assert.Equal(1, issueSignal.TypedInfo.Number);
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
        Assert.Equal(1, issueSignal.TypedInfo.Number);
    }
}

