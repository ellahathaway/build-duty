using BuildDuty.Core;
using BuildDuty.Core.Models;
using NSubstitute;
using Octokit;
using Xunit;

namespace BuildDuty.Tests;

public class StorageProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StorageProvider _provider;

    public StorageProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"build-duty-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var configProvider = Substitute.For<IBuildDutyConfigProvider>();
        // StorageProvider builds its root as ~/.build-duty/{Name},
        // so we use the temp dir as the config name to isolate tests.
        configProvider.GetConfig().Returns(new BuildDutyConfig { Name = _tempDir });
        _provider = new StorageProvider(configProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorkItemsAsync_Empty_ReturnsEmpty()
    {
        var items = await _provider.GetWorkItemsAsync();
        Assert.Empty(items);
    }

    [Fact]
    public async Task SaveAndGetWorkItem_RoundTrips()
    {
        var wi = new WorkItem { Id = "wi-1", SignalIds = ["sig-1", "sig-2"] };
        await _provider.SaveWorkItemAsync(wi);

        var loaded = await _provider.GetWorkItemAsync("wi-1");
        Assert.Equal("wi-1", loaded.Id);
        Assert.Equal(["sig-1", "sig-2"], loaded.SignalIds);
    }

    [Fact]
    public async Task GetWorkItemsAsync_ReturnsAll()
    {
        await _provider.SaveWorkItemAsync(new WorkItem { Id = "wi-a", SignalIds = ["sig-a"] });
        await _provider.SaveWorkItemAsync(new WorkItem { Id = "wi-b", SignalIds = ["sig-b"] });

        var items = (await _provider.GetWorkItemsAsync()).OrderBy(w => w.Id).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("wi-a", items[0].Id);
        Assert.Equal("wi-b", items[1].Id);
    }

    [Fact]
    public async Task SaveAndGetTriageRun_RoundTrips()
    {
        var run = new TriageRun { Id = "triage-1", SignalIds = ["sig-1", "sig-2"] };
        await _provider.SaveTriageRunAsync(run);

        var loaded = await _provider.GetTriageRunAsync("triage-1");
        Assert.Equal("triage-1", loaded.Id);
        Assert.Equal(["sig-1", "sig-2"], loaded.SignalIds);
    }

    [Fact]
    public async Task GetWorkItemAsync_NotFound_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _provider.GetWorkItemAsync("nonexistent"));
    }

    [Fact]
    public async Task SaveAndGetSignal_GitHubIssue_RoundTrips()
    {
        var user = new User();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var issue = new Issue(
            url: "https://api.github.com/repos/dotnet/runtime/issues/1",
            htmlUrl: "https://github.com/dotnet/runtime/issues/1",
            commentsUrl: "", eventsUrl: "",
            number: 1, state: ItemState.Open, title: "Test issue", body: "",
            closedBy: null, user: user, labels: [], assignee: null, assignees: [],
            milestone: null, comments: 0, pullRequest: null,
            closedAt: null, createdAt: createdAt, updatedAt: null,
            id: 1, nodeId: "", locked: false, repository: null,
            reactions: null, stateReason: null, activeLockReason: null);

        var signal = new GitHubIssueSignal(issue) { Summary = "A summary" };
        await _provider.SaveSignalAsync(signal);

        var loaded = (GitHubIssueSignal)await _provider.GetSignalAsync(signal.Id);
        Assert.Equal(signal.Id, loaded.Id);
        Assert.Equal(SignalType.GitHubIssue, loaded.Type);
        Assert.Equal("Test issue", loaded.Info.Title);
        Assert.Equal(ItemState.Open, loaded.Info.State.Value);
        Assert.Equal("A summary", loaded.Summary);
    }

    [Fact]
    public async Task SaveAndGetSignal_GitHubPullRequest_RoundTrips()
    {
        var user = new User();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var pr = new PullRequest(
            id: 10, nodeId: "",
            url: "https://api.github.com/repos/dotnet/sdk/pulls/10",
            htmlUrl: "https://github.com/dotnet/sdk/pull/10",
            diffUrl: "", patchUrl: "", issueUrl: "", statusesUrl: "",
            number: 10, state: ItemState.Open, title: "Update deps", body: "",
            createdAt: createdAt, updatedAt: createdAt,
            closedAt: null, mergedAt: null,
            head: null, @base: null, user: user,
            assignee: null, assignees: [], draft: false,
            mergeable: null, mergeableState: null, mergedBy: null,
            mergeCommitSha: "", comments: 0, commits: 0,
            additions: 0, deletions: 0, changedFiles: 0,
            milestone: null, locked: false, maintainerCanModify: null,
            requestedReviewers: [], requestedTeams: [], labels: [],
            activeLockReason: null);

        var signal = new GitHubPullRequestSignal(pr);
        await _provider.SaveSignalAsync(signal);

        var loaded = (GitHubPullRequestSignal)await _provider.GetSignalAsync(signal.Id);
        Assert.Equal(signal.Id, loaded.Id);
        Assert.Equal(SignalType.GitHubPullRequest, loaded.Type);
        Assert.Equal("Update deps", loaded.Info.Title);
        Assert.Equal(ItemState.Open, loaded.Info.State.Value);
    }

    [Fact]
    public async Task GetSignalJsonAsync_GitHubIssue_ReturnsJson()
    {
        var user = new User();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var issue = new Issue(
            url: "https://api.github.com/repos/dotnet/runtime/issues/2",
            htmlUrl: "https://github.com/dotnet/runtime/issues/2",
            commentsUrl: "", eventsUrl: "",
            number: 2, state: ItemState.Closed, title: "Closed issue", body: "",
            closedBy: null, user: user, labels: [], assignee: null, assignees: [],
            milestone: null, comments: 0, pullRequest: null,
            closedAt: null, createdAt: createdAt, updatedAt: null,
            id: 2, nodeId: "", locked: false, repository: null,
            reactions: null, stateReason: null, activeLockReason: null);

        var signal = new GitHubIssueSignal(issue);
        await _provider.SaveSignalAsync(signal);

        var json = await _provider.GetSignalJsonAsync(signal.Id);
        Assert.Contains("\"title\": \"Closed issue\"", json);
        Assert.Contains("\"state\": \"closed\"", json);
    }
}
