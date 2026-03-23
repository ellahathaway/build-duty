using BuildDuty.Core;
using Xunit;

namespace BuildDuty.Tests;

public class WorkItemStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkItemStore _store;

    public WorkItemStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"buildduty_test_{Guid.NewGuid():N}");
        _store = new WorkItemStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var wi = new WorkItem
        {
            Id = "wi_roundtrip",
            Status = "new",
            Title = "Round-trip test",
            CorrelationId = "corr_123",
            Signals =
            [
                new SignalReference { Type = "pipeline-failure", Ref = "ado://run/42" }
            ]
        };

        await _store.SaveAsync(wi);
        var loaded = await _store.LoadAsync("wi_roundtrip");

        Assert.NotNull(loaded);
        Assert.Equal("wi_roundtrip", loaded.Id);
        Assert.Equal("new", loaded.Status);
        Assert.False(loaded.IsResolved);
        Assert.Equal("Round-trip test", loaded.Title);
        Assert.Equal("corr_123", loaded.CorrelationId);
        Assert.Single(loaded.Signals);
        Assert.Equal("pipeline-failure", loaded.Signals[0].Type);
    }

    [Fact]
    public async Task Load_NonExistent_ReturnsNull()
    {
        var result = await _store.LoadAsync("wi_missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_FiltersByResolved()
    {
        await _store.SaveAsync(new WorkItem { Id = "wi_a", Status = "new", Title = "A" });
        await _store.SaveAsync(new WorkItem { Id = "wi_b", Status = "investigating", Title = "B" });
        await _store.SaveAsync(new WorkItem { Id = "wi_c", Status = "fixed", Title = "C" });

        var unresolved = await _store.ListAsync(resolved: false);
        var resolved = await _store.ListAsync(resolved: true);

        Assert.Equal(2, unresolved.Count);
        Assert.Single(resolved);
    }

    [Fact]
    public async Task ListAsync_RespectsLimit()
    {
        await _store.SaveAsync(new WorkItem { Id = "wi_1", Title = "One" });
        await _store.SaveAsync(new WorkItem { Id = "wi_2", Title = "Two" });
        await _store.SaveAsync(new WorkItem { Id = "wi_3", Title = "Three" });

        var items = await _store.ListAsync(limit: 2);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task SaveAsync_PersistsStatusChanges()
    {
        var wi = new WorkItem
        {
            Id = "wi_transitions",
            Status = "new",
            Title = "Transition persistence"
        };

        wi.SetStatus("investigating", "starting");
        await _store.SaveAsync(wi);

        var loaded = await _store.LoadAsync("wi_transitions");
        Assert.NotNull(loaded);
        Assert.Equal("investigating", loaded.Status);
        Assert.Single(loaded.History);
        Assert.Equal("status-change", loaded.History[0].Action);
    }

    [Fact]
    public async Task Exists_ReturnsTrueForSavedItem()
    {
        var wi = new WorkItem { Id = "wi_exists", Title = "Exists test" };
        await _store.SaveAsync(wi);

        Assert.True(_store.Exists("wi_exists"));
        Assert.False(_store.Exists("wi_nope"));
    }
}
