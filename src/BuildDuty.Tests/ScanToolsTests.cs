using BuildDuty.AI;
using BuildDuty.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace BuildDuty.Tests;

public class ScanToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkItemStore _store;

    public ScanToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"buildduty_scan_{Guid.NewGuid():N}");
        _store = new WorkItemStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static AIFunctionArguments Args(params (string Key, object Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => (object?)p.Value));

    [Fact]
    public void ScanTools_ReturnsExpectedToolNames()
    {
        var tools = ScanTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("create_work_item", names);
        Assert.Contains("resolve_work_item", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void BuildDutyTools_ReturnsExpectedToolNames()
    {
        var tools = BuildDutyTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("get_work_item", names);
        Assert.Contains("list_work_items", names);
        Assert.Contains("work_item_exists", names);
    }

    [Fact]
    public void TriageTools_ReturnsExpectedToolNames()
    {
        var tools = TriageTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("get_signals", names);
    }

    [Fact]
    public async Task CreateWorkItem_SavesNewItem()
    {
        var tools = ScanTools.Create(_store);
        var createTool = tools.First(t => t.Name == "create_work_item");

        var result = await createTool.InvokeAsync(Args(
            ("id", "wi_test_1"),
            ("title", "Test failure"),
            ("correlationId", "corr_test_1"),
            ("signalType", "ado-pipeline-run"),
            ("signalRef", "https://dev.azure.com/test")));

        Assert.Contains("Created", result?.ToString());
        Assert.True(_store.Exists("wi_test_1"));

        var item = await _store.LoadAsync("wi_test_1");
        Assert.NotNull(item);
        Assert.Equal("Test failure", item.Title);
        Assert.Equal("new", item.Status);
        Assert.False(item.IsResolved);
        Assert.Equal("corr_test_1", item.CorrelationId);
    }

    [Fact]
    public async Task CreateWorkItem_SkipsDuplicate()
    {
        await _store.SaveAsync(new WorkItem
        {
            Id = "wi_dup_1",
            Title = "Existing",
            CorrelationId = "corr_dup_1"
        });

        var tools = ScanTools.Create(_store);
        var createTool = tools.First(t => t.Name == "create_work_item");

        var result = await createTool.InvokeAsync(Args(
            ("id", "wi_dup_1"),
            ("title", "Duplicate"),
            ("correlationId", "corr_dup_1"),
            ("signalType", "test"),
            ("signalRef", "https://example.com")));

        Assert.Contains("already exists", result?.ToString());

        var item = await _store.LoadAsync("wi_dup_1");
        Assert.Equal("Existing", item!.Title);
    }

    [Fact]
    public async Task WorkItemExists_ReturnsTrueForExisting()
    {
        await _store.SaveAsync(new WorkItem { Id = "wi_check_1", Title = "Test" });

        var tools = BuildDutyTools.Create(_store);
        var existsTool = tools.First(t => t.Name == "work_item_exists");

        var result = await existsTool.InvokeAsync(Args(("id", "wi_check_1")));

        Assert.Contains("exists", result?.ToString());
        Assert.DoesNotContain("does not", result?.ToString());
    }

    [Fact]
    public async Task WorkItemExists_ReturnsFalseForMissing()
    {
        var tools = BuildDutyTools.Create(_store);
        var existsTool = tools.First(t => t.Name == "work_item_exists");

        var result = await existsTool.InvokeAsync(Args(("id", "wi_nonexistent")));

        Assert.Contains("does not exist", result?.ToString());
    }

    [Fact]
    public async Task ResolveWorkItem_TransitionsToResolved()
    {
        await _store.SaveAsync(new WorkItem
        {
            Id = "wi_resolve_1",
            Title = "Failing build",
            Status = "new",
            CorrelationId = "corr_resolve_1"
        });

        var tools = ScanTools.Create(_store);
        var resolveTool = tools.First(t => t.Name == "resolve_work_item");

        var result = await resolveTool.InvokeAsync(Args(
            ("id", "wi_resolve_1"),
            ("reason", "Auto-resolved: latest build succeeded")));

        Assert.Contains("Resolved", result?.ToString());

        var item = await _store.LoadAsync("wi_resolve_1");
        Assert.Equal("resolved", item!.Status);
        Assert.True(item.IsResolved);
        Assert.Single(item.History);
    }

    [Fact]
    public async Task ResolveWorkItem_SkipsAlreadyResolved()
    {
        var item = new WorkItem
        {
            Id = "wi_already_resolved",
            Title = "Old issue",
            Status = "new",
        };
        item.SetStatus("resolved", "test");
        await _store.SaveAsync(item);

        var tools = ScanTools.Create(_store);
        var resolveTool = tools.First(t => t.Name == "resolve_work_item");

        var result = await resolveTool.InvokeAsync(Args(
            ("id", "wi_already_resolved"),
            ("reason", "Should not happen")));

        Assert.Contains("already resolved", result?.ToString());
    }

    [Fact]
    public async Task ListWorkItems_FiltersAndReturns()
    {
        await _store.SaveAsync(new WorkItem
        {
            Id = "wi_list_1",
            Title = "Unresolved item",
            Status = "new",
            CorrelationId = "corr_1"
        });

        var resolved = new WorkItem
        {
            Id = "wi_list_2",
            Title = "Resolved item",
            Status = "new",
            CorrelationId = "corr_2"
        };
        resolved.SetStatus("fixed", "done");
        await _store.SaveAsync(resolved);

        var tools = BuildDutyTools.Create(_store);
        var listTool = tools.First(t => t.Name == "list_work_items");

        var result = await listTool.InvokeAsync(Args(
            ("status", "unresolved"),
            ("limit", 10)));

        var text = result?.ToString()!;
        Assert.Contains("wi_list_1", text);
        Assert.DoesNotContain("wi_list_2", text);
    }
}
