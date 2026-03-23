using BuildDuty.Core;
using Xunit;

namespace BuildDuty.Tests;

public class WorkItemStatusTests
{
    [Theory]
    [InlineData("new", false)]
    [InlineData("tracked", false)]
    [InlineData("investigating", false)]
    [InlineData("needs-review", false)]
    [InlineData("test-failures", false)]
    [InlineData("resolved", true)]
    [InlineData("fixed", true)]
    [InlineData("merged", true)]
    [InlineData("closed", true)]
    public void IsResolved_ReflectsTerminalStatuses(string status, bool expected)
    {
        var wi = new WorkItem { Id = "wi_test", Status = status, Title = "Test" };
        Assert.Equal(expected, wi.IsResolved);
    }

    [Fact]
    public void SetStatus_UpdatesStatusAndHistory()
    {
        var wi = new WorkItem { Id = "wi_test", Status = "new", Title = "Test" };

        wi.SetStatus("tracked", "acknowledged");

        Assert.Equal("tracked", wi.Status);
        Assert.Single(wi.History);
        Assert.Equal("status-change", wi.History[0].Action);
        Assert.Equal("new", wi.History[0].From);
        Assert.Equal("tracked", wi.History[0].To);
        Assert.Equal("acknowledged", wi.History[0].Note);
    }

    [Fact]
    public void FullLifecycle_TracksHistory()
    {
        var wi = new WorkItem { Id = "wi_lifecycle", Status = "new", Title = "Lifecycle test" };

        wi.SetStatus("investigating", "AI job started");
        wi.SetStatus("fixed", "Investigation complete");

        Assert.True(wi.IsResolved);
        Assert.Equal(2, wi.History.Count);
        Assert.Equal("new", wi.History[0].From);
        Assert.Equal("investigating", wi.History[0].To);
        Assert.Equal("investigating", wi.History[1].From);
        Assert.Equal("fixed", wi.History[1].To);
    }

    [Fact]
    public void SetStatus_SetsTimestamp()
    {
        var wi = new WorkItem { Id = "wi_ts", Title = "Timestamp test" };
        var before = DateTime.UtcNow;

        wi.SetStatus("tracked");

        Assert.True(wi.History[0].TimestampUtc >= before);
        Assert.True(wi.History[0].TimestampUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void DefaultStatus_IsNew()
    {
        var wi = new WorkItem { Id = "wi_default", Title = "Default test" };
        Assert.Equal("new", wi.Status);
        Assert.False(wi.IsResolved);
    }
}
