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
}
