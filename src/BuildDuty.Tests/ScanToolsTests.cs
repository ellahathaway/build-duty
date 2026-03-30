using BuildDuty.AI;
using BuildDuty.Core;
using BuildDuty.Core.Models;
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
        Directory.CreateDirectory(_tempDir);
        _store = new WorkItemStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static AIFunctionArguments Args(params (string Key, object Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => (object?)p.Value));

    [Fact]
    public void WorkItemTriageTools_ReturnsExpectedToolNames()
    {
        var tools = WorkItemTriageTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("resolve_work_item", names);
        Assert.Contains("update_work_item_status", names);
        Assert.Contains("link_work_items", names);
        Assert.Equal(3, names.Count);
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
    public void SummarizeTools_ReturnsExpectedToolNames()
    {
        var tools = SummarizeTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("set_work_item_summary", names);
        Assert.Contains("get_task_log", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void TriageTools_ReturnsExpectedToolNames()
    {
        var tools = TriageTools.Create(_store);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("get_sources", names);
    }

    [Fact]
    public async Task CollectionCreatesWorkItems()
    {
        // Simulate what collectors do: create work items directly in the store
        var source = new CollectedSource
        {
            Id = "wi_test_1",
            Title = "Test failure",
            CorrelationId = "corr_test_1",
            SourceType = "ado-pipeline-run",
            SourceRef = "https://dev.azure.com/test",
            Status = "failed",
        };

        Assert.False(_store.Exists(source.Id));

        await _store.SaveAsync(new WorkItem
        {
            Id = source.Id,
            Status = "new",
            Title = source.Title,
            CorrelationId = source.CorrelationId,
            Sources = [new SourceReference { Type = source.SourceType, Ref = source.SourceRef }],
        });

        Assert.True(_store.Exists("wi_test_1"));
        var item = await _store.LoadAsync("wi_test_1");
        Assert.NotNull(item);
        Assert.Equal("Test failure", item.Title);
        Assert.Equal("new", item.Status);
        Assert.Equal("corr_test_1", item.CorrelationId);
    }

    [Fact]
    public async Task CollectionSkipsDuplicateWorkItems()
    {
        await _store.SaveAsync(new WorkItem
        {
            Id = "wi_dup_1",
            Title = "Existing",
            CorrelationId = "corr_dup_1"
        });

        // Collection checks Exists() before creating
        Assert.True(_store.Exists("wi_dup_1"));

        // Should NOT overwrite — collector skips if Exists()
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

        var tools = WorkItemTriageTools.Create(_store);
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

        var tools = WorkItemTriageTools.Create(_store);
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

    [Theory]
    [InlineData("https://dev.azure.com/dnceng/internal/_build/results?buildId=12345",
        "https://dev.azure.com/dnceng", "internal", 12345)]
    [InlineData("https://dnceng.visualstudio.com/internal/_build/results?buildId=99",
        "https://dev.azure.com/dnceng", "internal", 99)]
    public void ParseBuildUrl_ExtractsComponents(string url, string expectedOrg, string expectedProject, int expectedBuildId)
    {
        var result = AzureDevOpsBuildClient.ParseBuildUrl(url);
        Assert.NotNull(result);
        Assert.Equal(expectedOrg, result.Value.OrgUrl);
        Assert.Equal(expectedProject, result.Value.Project);
        Assert.Equal(expectedBuildId, result.Value.BuildId);
    }

    [Fact]
    public void ParseBuildUrl_ReturnsNullForInvalid()
    {
        Assert.Null(AzureDevOpsBuildClient.ParseBuildUrl("https://github.com/dotnet/runtime"));
    }
}
