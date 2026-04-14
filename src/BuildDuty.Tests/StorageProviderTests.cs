using BuildDuty.Core;
using BuildDuty.Core.Models;
using NSubstitute;
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
        configProvider.Get().Returns(new BuildDutyConfig { Name = _tempDir });
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
        var wi = new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis("sig-1", []), new LinkedAnalysis("sig-2", [])] };
        await _provider.SaveWorkItemAsync(wi);

        var loaded = await _provider.GetWorkItemAsync("wi-1");
        Assert.Equal("wi-1", loaded.Id);
        Assert.Equal(2, loaded.LinkedAnalyses.Count);
    }

    [Fact]
    public async Task GetWorkItemsAsync_ReturnsAll()
    {
        await _provider.SaveWorkItemAsync(new WorkItem { Id = "wi-a", LinkedAnalyses = [new LinkedAnalysis("sig-a", [])] });
        await _provider.SaveWorkItemAsync(new WorkItem { Id = "wi-b", LinkedAnalyses = [new LinkedAnalysis("sig-b", [])] });

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
    public async Task SaveAndGetSignal_RoundTripsInfoJson()
    {
        var info = new GitHubIssueInfo(1, "Test issue", "Open", null, null, null);
        var signal = new GitHubIssueSignal(info, new Uri("https://github.com/dotnet/runtime/issues/1"))
        {
            Id = "sig-1",
        };

        await _provider.SaveSignalAsync(signal);

        var loaded = await _provider.GetSignalAsync("sig-1");
        var issueSignal = Assert.IsType<GitHubIssueSignal>(loaded);

        Assert.Equal(new Uri("https://github.com/dotnet/runtime/issues/1"), issueSignal.Url);
        Assert.Equal("Open", issueSignal.TypedInfo.State);
        Assert.Equal("Test issue", issueSignal.TypedInfo.Title);
    }

    [Fact]
    public async Task GitHubIssueSignal_TypedInfo_SurvivesRoundTrip()
    {
        var updatedAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var info = new GitHubIssueInfo(42, "Round-trip test issue", "Open", updatedAt, null, null);

        var signal = new GitHubIssueSignal(info, new Uri("https://github.com/dotnet/runtime/issues/42")) { Id = "sig-rt-issue" };
        await _provider.SaveSignalAsync(signal);

        var loaded = await _provider.GetSignalAsync("sig-rt-issue");
        var loadedIssue = Assert.IsType<GitHubIssueSignal>(loaded);

        Assert.Equal(new Uri("https://github.com/dotnet/runtime/issues/42"), loadedIssue.Url);
        Assert.Equal("Open", loadedIssue.TypedInfo.State);
        Assert.Equal(updatedAt, loadedIssue.TypedInfo.UpdatedAt);
        Assert.Equal(42, loadedIssue.TypedInfo.Number);
        Assert.Equal("Round-trip test issue", loadedIssue.TypedInfo.Title);
    }

    [Fact]
    public async Task GitHubPullRequestSignal_TypedInfo_SurvivesRoundTrip()
    {
        var updatedAt = new DateTimeOffset(2026, 4, 1, 14, 0, 0, TimeSpan.Zero);
        var info = new GitHubPullRequestInfo(99, "Round-trip test PR", "Closed", updatedAt, false, null, null, null);

        var signal = new GitHubPullRequestSignal(info, new Uri("https://github.com/dotnet/sdk/pull/99")) { Id = "sig-rt-pr" };
        await _provider.SaveSignalAsync(signal);

        var loaded = await _provider.GetSignalAsync("sig-rt-pr");
        var loadedPr = Assert.IsType<GitHubPullRequestSignal>(loaded);

        Assert.Equal(new Uri("https://github.com/dotnet/sdk/pull/99"), loadedPr.Url);
        Assert.Equal("Closed", loadedPr.TypedInfo.State);
        Assert.Equal(updatedAt, loadedPr.TypedInfo.UpdatedAt);
        Assert.Equal(99, loadedPr.TypedInfo.Number);
        Assert.Equal("Round-trip test PR", loadedPr.TypedInfo.Title);
    }

    [Fact]
    public async Task Signal_Analyses_PersistAndRoundTrip()
    {
        var info = new GitHubIssueInfo(1, "Test", "Open", null, null, null);
        var signal = new GitHubIssueSignal(info, new Uri("https://github.com/dotnet/runtime/issues/1")) { Id = "sig-analyses" };

        var analysisData = System.Text.Json.JsonSerializer.SerializeToElement(new { issueNumber = 1, errorMessages = new[] { "error CS0246" } });
        signal.Analyses.Add(new SignalAnalysis(analysisData, "Compiler error CS0246"));

        await _provider.SaveSignalAsync(signal);
        var loaded = await _provider.GetSignalAsync("sig-analyses");

        Assert.Single(loaded.Analyses);
        Assert.Equal("Compiler error CS0246", loaded.Analyses[0].Analysis);
        Assert.False(string.IsNullOrEmpty(loaded.Analyses[0].Id));
    }

    [Fact]
    public async Task Signal_Analyses_PreservedAcrossSaves()
    {
        var info = new GitHubIssueInfo(1, "Test", "Open", null, null, null);
        var signal = new GitHubIssueSignal(info, new Uri("https://github.com/dotnet/runtime/issues/1")) { Id = "sig-preserve" };

        var data1 = System.Text.Json.JsonSerializer.SerializeToElement(new { error = "first" });
        signal.Analyses.Add(new SignalAnalysis(data1, "First analysis"));

        await _provider.SaveSignalAsync(signal);

        // Simulate collector creating a new signal object with same ID (preserving analyses)
        var updatedInfo = new GitHubIssueInfo(1, "Test", "Closed", null, null, null);
        var updatedSignal = new GitHubIssueSignal(updatedInfo, new Uri("https://github.com/dotnet/runtime/issues/1"));
        updatedSignal.PreserveFrom(signal);

        var data2 = System.Text.Json.JsonSerializer.SerializeToElement(new { error = "second" });
        updatedSignal.Analyses.Add(new SignalAnalysis(data2, "Second analysis"));

        await _provider.SaveSignalAsync(updatedSignal);
        var loaded = await _provider.GetSignalAsync("sig-preserve");

        Assert.Equal(2, loaded.Analyses.Count);
        Assert.Equal("First analysis", loaded.Analyses[0].Analysis);
        Assert.Equal("Second analysis", loaded.Analyses[1].Analysis);
    }

    [Fact]
    public async Task Signal_Analysis_IdIsStableAfterRoundTrip()
    {
        var info = new GitHubIssueInfo(1, "Test", "Open", null, null, null);
        var signal = new GitHubIssueSignal(info, new Uri("https://github.com/dotnet/runtime/issues/1")) { Id = "sig-stable-id" };

        var data = System.Text.Json.JsonSerializer.SerializeToElement(new { err = "test" });
        var analysis = new SignalAnalysis(data, "Test analysis");
        var originalId = analysis.Id;
        signal.Analyses.Add(analysis);

        await _provider.SaveSignalAsync(signal);
        var loaded = await _provider.GetSignalAsync("sig-stable-id");

        Assert.Equal(originalId, loaded.Analyses[0].Id);
    }

    [Fact]
    public async Task WorkItem_LinkedAnalyses_RoundTrip()
    {
        var wi = new WorkItem
        {
            Id = "wi-la",
            LinkedAnalyses = [
                new LinkedAnalysis("sig-1", ["analysis_abc", "analysis_def"]),
                new LinkedAnalysis("sig-2", ["analysis_ghi"])
            ]
        };

        await _provider.SaveWorkItemAsync(wi);
        var loaded = await _provider.GetWorkItemAsync("wi-la");

        Assert.Equal(2, loaded.LinkedAnalyses.Count);
        var first = loaded.LinkedAnalyses.First(la => la.SignalId == "sig-1");
        Assert.Equal(["analysis_abc", "analysis_def"], first.AnalysisIds);
        var second = loaded.LinkedAnalyses.First(la => la.SignalId == "sig-2");
        Assert.Equal(["analysis_ghi"], second.AnalysisIds);
    }
}
