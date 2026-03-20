using BuildDuty.Core;
using Xunit;

namespace BuildDuty.Tests;

public class WorkItemStateTransitionTests
{
    [Theory]
    [InlineData(WorkItemState.Unresolved, WorkItemState.InProgress)]
    [InlineData(WorkItemState.InProgress, WorkItemState.Resolved)]
    [InlineData(WorkItemState.InProgress, WorkItemState.Unresolved)]
    public void ValidTransitions_Succeed(WorkItemState from, WorkItemState to)
    {
        var wi = new WorkItem
        {
            Id = "wi_test",
            State = from,
            Title = "Test work item"
        };

        wi.TransitionTo(to, "test transition");

        Assert.Equal(to, wi.State);
        Assert.Single(wi.History);
        Assert.Equal("state-change", wi.History[0].Action);
        Assert.Equal(from.ToString().ToLowerInvariant(), wi.History[0].From);
        Assert.Equal(to.ToString().ToLowerInvariant(), wi.History[0].To);
        Assert.Equal("test transition", wi.History[0].Note);
    }

    [Theory]
    [InlineData(WorkItemState.Unresolved, WorkItemState.Resolved)]
    [InlineData(WorkItemState.Resolved, WorkItemState.InProgress)]
    [InlineData(WorkItemState.Resolved, WorkItemState.Unresolved)]
    [InlineData(WorkItemState.Unresolved, WorkItemState.Unresolved)]
    public void InvalidTransitions_Throw(WorkItemState from, WorkItemState to)
    {
        var wi = new WorkItem
        {
            Id = "wi_test",
            State = from,
            Title = "Test work item"
        };

        Assert.Throws<InvalidOperationException>(() => wi.TransitionTo(to));
    }

    [Fact]
    public void FullLifecycle_TracksHistory()
    {
        var wi = new WorkItem
        {
            Id = "wi_lifecycle",
            State = WorkItemState.Unresolved,
            Title = "Lifecycle test"
        };

        wi.TransitionTo(WorkItemState.InProgress, "AI job started");
        wi.TransitionTo(WorkItemState.Resolved, "Investigation complete");

        Assert.Equal(WorkItemState.Resolved, wi.State);
        Assert.Equal(2, wi.History.Count);
        Assert.Equal("unresolved", wi.History[0].From);
        Assert.Equal("inprogress", wi.History[0].To);
        Assert.Equal("inprogress", wi.History[1].From);
        Assert.Equal("resolved", wi.History[1].To);
    }

    [Fact]
    public void TransitionTo_SetsTimestamp()
    {
        var wi = new WorkItem
        {
            Id = "wi_ts",
            State = WorkItemState.Unresolved,
            Title = "Timestamp test"
        };
        var before = DateTime.UtcNow;

        wi.TransitionTo(WorkItemState.InProgress);

        Assert.True(wi.History[0].TimestampUtc >= before);
        Assert.True(wi.History[0].TimestampUtc <= DateTime.UtcNow);
    }

    [Fact]
    public void ReturnToUnresolved_FromInProgress()
    {
        var wi = new WorkItem
        {
            Id = "wi_return",
            State = WorkItemState.Unresolved,
            Title = "Return test"
        };

        wi.TransitionTo(WorkItemState.InProgress);
        wi.TransitionTo(WorkItemState.Unresolved, "AI run failed, returning");

        Assert.Equal(WorkItemState.Unresolved, wi.State);
        Assert.Equal(2, wi.History.Count);
    }
}
