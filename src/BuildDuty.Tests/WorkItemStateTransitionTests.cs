using BuildDuty.Core;
using Xunit;

namespace BuildDuty.Tests;

public class WorkItemStatusTests
{
    [Theory]
    [InlineData("new", false)]
    [InlineData("tracked", false)]
    [InlineData("needs-investigation", false)]
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

        wi.SetStatus("needs-investigation", "AI job started");
        wi.SetStatus("fixed", "Investigation complete");

        Assert.True(wi.IsResolved);
        Assert.Equal(2, wi.History.Count);
        Assert.Equal("new", wi.History[0].From);
        Assert.Equal("needs-investigation", wi.History[0].To);
        Assert.Equal("needs-investigation", wi.History[1].From);
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
        Assert.Null(wi.State);
        Assert.False(wi.IsResolved);
    }

    [Fact]
    public void NeedsTriage_TrueWhenStateIsSet()
    {
        var wi = new WorkItem { Id = "wi_state", Title = "State test", TriagedAtUtc = DateTime.UtcNow };
        Assert.False(wi.NeedsTriage);

        wi.State = "updated";
        Assert.True(wi.NeedsTriage);

        wi.State = "stable";
        Assert.False(wi.NeedsTriage);

        wi.State = null;
        Assert.False(wi.NeedsTriage);
    }

    [Fact]
    public void NeedsSummary_FalseWhenStateClosed()
    {
        var wi = new WorkItem { Id = "wi_closed", Title = "Closed test", State = "closed" };
        Assert.False(wi.NeedsSummary);
    }

    [Fact]
    public void NeedsSummary_FalseWhenStateStable()
    {
        var wi = new WorkItem { Id = "wi_stable", Title = "Stable test", State = "stable", Summary = "Already summarized" };
        Assert.False(wi.NeedsSummary);
    }

    [Fact]
    public void NeedsSummary_TrueWhenStateNew()
    {
        var wi = new WorkItem { Id = "wi_new", Title = "New test", State = "new" };
        Assert.True(wi.NeedsSummary);
    }
}
