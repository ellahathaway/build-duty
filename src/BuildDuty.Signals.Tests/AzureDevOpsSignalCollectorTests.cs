using System.Reflection;
using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using BuildDuty.Signals.Collection;
using Maestro.Common;
using Microsoft.Deployment.DotNet.Releases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.Build.WebApi;
using Xunit;

namespace BuildDuty.Signals.Tests;

public class AzureDevOpsSignalCollectorTests
{
    [Theory]
    [InlineData("1d", 1, 0, 0)]
    [InlineData("2h", 0, 2, 0)]
    [InlineData("30m", 0, 0, 30)]
    [InlineData("1d2h30m", 1, 2, 30)]
    [InlineData("", 0, 0, 0)]
    public void ParseAge_ParsesExpectedSpan(string input, int days, int hours, int minutes)
    {
        var span = AzureDevOpsSignalCollector.ParseAge(input);

        Assert.Equal(TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes), span);
    }

    [Fact]
    public void MatchesTimelineFilter_MatchesTypeStatusAndName()
    {
        var record = new TimelineRecord
        {
            Id = Guid.NewGuid(),
            RecordType = "Job",
            Name = "Build x64",
            Result = TaskResult.Failed,
        };

        var filters = new List<TimelineFilter>
        {
            new()
            {
                Type = TimelineRecordType.Job,
                Status = [TaskResult.Failed],
                Names = ["^Build.*"],
            },
        };

        var method = typeof(AzureDevOpsSignalCollector).GetMethod(
            "MatchesTimelineFilter",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected MatchesTimelineFilter to exist.");

        var result = (bool?)method.Invoke(null, [record, filters])
            ?? throw new InvalidOperationException("MatchesTimelineFilter invocation returned null.");

        Assert.True(result);
    }

    [Fact]
    public void MatchesTimelineFilter_ReturnsFalse_WhenTypeMismatch()
    {
        var record = new TimelineRecord
        {
            Id = Guid.NewGuid(),
            RecordType = "Stage",
            Name = "Build x64",
            Result = TaskResult.Failed,
        };

        var filters = new List<TimelineFilter>
        {
            new()
            {
                Type = TimelineRecordType.Job,
                Status = [TaskResult.Failed],
                Names = ["^Build.*"],
            },
        };

        var result = InvokeMatchesTimelineFilter(record, filters);
        Assert.False(result);
    }

    [Fact]
    public void MatchesTimelineFilter_ReturnsFalse_WhenStatusMismatch()
    {
        var record = new TimelineRecord
        {
            Id = Guid.NewGuid(),
            RecordType = "Job",
            Name = "Build x64",
            Result = TaskResult.Succeeded,
        };

        var filters = new List<TimelineFilter>
        {
            new()
            {
                Type = TimelineRecordType.Job,
                Status = [TaskResult.Failed],
                Names = ["^Build.*"],
            },
        };

        var result = InvokeMatchesTimelineFilter(record, filters);
        Assert.False(result);
    }

    [Fact]
    public void MatchesTimelineFilter_ReturnsFalse_WhenNameRegexMismatch()
    {
        var record = new TimelineRecord
        {
            Id = Guid.NewGuid(),
            RecordType = "Job",
            Name = "Test x64",
            Result = TaskResult.Failed,
        };

        var filters = new List<TimelineFilter>
        {
            new()
            {
                Type = TimelineRecordType.Job,
                Status = [TaskResult.Failed],
                Names = ["^Build.*"],
            },
        };

        var result = InvokeMatchesTimelineFilter(record, filters);
        Assert.False(result);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1x")]
    [InlineData("-1d")]
    public void ParseAge_MalformedInput_ReturnsZeroish(string input)
    {
        var span = AzureDevOpsSignalCollector.ParseAge(input);
        Assert.Equal(TimeSpan.Zero, span);
    }

    [Fact]
    public void ParseAge_PartiallyMalformedInput_ParsesRecognizedSegments()
    {
        var span = AzureDevOpsSignalCollector.ParseAge("1d2q");
        Assert.Equal(TimeSpan.FromDays(1), span);
    }

    [Fact]
    public async Task CollectAsync_NoOrganizations_ReturnsEmpty()
    {
        var collector = new AzureDevOpsSignalCollector(
            new AzureDevOpsConfig { Organizations = [] },
            new StubTokenProvider(),
            NullLogger.Instance,
            new ReleaseBranchResolver(new StubTokenProvider(), NullLogger.Instance));

        var result = await collector.CollectAsync();

        Assert.Empty(result.Signals);
    }

    [Fact]
    public async Task CollectAsync_NonEmptyConfig_NoBranchesAndNoRelease_AttemptsCollection()
    {
        // When no branches and no release are configured, the collector should
        // attempt to query (not silently skip). With a stub token provider that
        // can't connect, this surfaces as a failure rather than being silently ignored.
        var collector = new AzureDevOpsSignalCollector(
            new AzureDevOpsConfig
            {
                Organizations =
                [
                    new AzureDevOpsOrganizationConfig
                    {
                        Url = "https://dev.azure.com/test",
                        Projects =
                        [
                            new AzureDevOpsProjectConfig
                            {
                                Name = "test-project",
                                Pipelines =
                                [
                                    new AzureDevOpsPipelineConfig
                                    {
                                        Id = 1,
                                        Name = "pipeline",
                                        Branches = [],
                                        Release = null,
                                        Status = [BuildResult.Failed],
                                        TimelineResults = [TaskResult.Failed],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
            new StubTokenProvider(),
            NullLogger.Instance,
            new ReleaseBranchResolver(new StubTokenProvider(), NullLogger.Instance));

        var result = await collector.CollectAsync();

        // The pipeline should not be silently skipped — it should report a failure
        // since the stub token provider cannot create a real connection.
        Assert.NotEmpty(result.Failures);
        Assert.Contains(result.Failures, f => f.ScopeKey.Contains("test-project/1"));
    }

    private static bool InvokeMatchesTimelineFilter(TimelineRecord record, List<TimelineFilter> filters)
    {
        var method = typeof(AzureDevOpsSignalCollector).GetMethod(
            "MatchesTimelineFilter",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected MatchesTimelineFilter to exist.");

        return (bool?)method.Invoke(null, [record, filters])
            ?? throw new InvalidOperationException("MatchesTimelineFilter invocation returned null.");
    }

    private sealed class StubTokenProvider : IRemoteTokenProvider
    {
        public string GetTokenForRepository(string repoUri) => "test-token";
        public Task<string?> GetTokenForRepositoryAsync(string repoUri) => Task.FromResult<string?>("test-token");
    }
}
