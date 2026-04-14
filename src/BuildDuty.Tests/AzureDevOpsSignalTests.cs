using System.Text.RegularExpressions;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;
using Xunit;

namespace BuildDuty.Tests;

public class AzureDevOpsPipelineSignalTests
{
    private static Build CreateBuild(int id, BuildResult result) => new()
    {
        Id = id,
        Result = result,
    };

    private static TimelineRecord CreateTimelineRecord(Guid? id = null, TaskResult result = TaskResult.Failed, string type = "Job", string name = "Build") => new()
    {
        Id = id ?? Guid.NewGuid(),
        Result = result,
        RecordType = type,
        Name = name,
    };

    private static Timeline CreateTimeline(params TimelineRecord[] records)
    {
        var timeline = (Timeline)Activator.CreateInstance(typeof(Timeline), nonPublic: true)!;
        foreach (var r in records)
        {
            timeline.Records.Add(r);
        }
        return timeline;
    }

    private static AzureDevOpsTimelineRecordInfo ToInfoRecord(TimelineRecord record) => new(
        record.Id,
        record.Result,
        record.RecordType,
        record.Name,
        [],
        record.Log?.Id);

    private static AzureDevOpsConfig CreateConfig(int pipelineId, List<string> branches, List<BuildResult>? status = null) => new()
    {
        Organizations =
        [
            new AzureDevOpsOrganizationConfig
            {
                Url = "https://dev.azure.com/testorg",
                Projects =
                [
                    new AzureDevOpsProjectConfig
                    {
                        Name = "TestProject",
                        Pipelines =
                        [
                            new AzureDevOpsPipelineConfig
                            {
                                Id = pipelineId,
                                Name = "TestPipeline",
                                Branches = branches,
                                Status = status ?? [BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled],
                                TimelineFilters =
                                [
                                    new TimelineFilter { Type = TimelineRecordType.Job, Names = [new Regex(".*")] }
                                ],
                            }
                        ]
                    }
                ]
            }
        ]
    };

    private class TestableAzureDevOpsCollector : AzureDevOpsSignalCollector
    {
        private readonly BuildHttpClient _buildClient;

        public TestableAzureDevOpsCollector(
            AzureDevOpsConfig config,
            IStorageProvider storageProvider,
            BuildHttpClient buildClient)
            : base(config, Substitute.For<IRemoteTokenProvider>(), storageProvider, new ReleaseBranchResolver())
        {
            _buildClient = buildClient;
        }

        protected override Task<BuildHttpClient> GetBuildClientAsync(string organizationUrl)
            => Task.FromResult(_buildClient);
    }

    private static BuildHttpClient CreateMockBuildClient(Build? latestBuild, Timeline? timeline = null)
    {
        var client = Substitute.For<BuildHttpClient>(new Uri("https://dev.azure.com/test"), new VssCredentials());

        client.GetBuildsAsync(
            project: Arg.Any<string>(),
            definitions: Arg.Any<IEnumerable<int>>(),
            queues: Arg.Any<IEnumerable<int>>(),
            buildNumber: Arg.Any<string>(),
            minFinishTime: Arg.Any<DateTime?>(),
            maxFinishTime: Arg.Any<DateTime?>(),
            requestedFor: Arg.Any<string>(),
            reasonFilter: Arg.Any<BuildReason?>(),
            statusFilter: Arg.Any<BuildStatus?>(),
            resultFilter: Arg.Any<BuildResult?>(),
            tagFilters: Arg.Any<IEnumerable<string>>(),
            properties: Arg.Any<IEnumerable<string>>(),
            top: Arg.Any<int?>(),
            continuationToken: Arg.Any<string>(),
            maxBuildsPerDefinition: Arg.Any<int?>(),
            deletedFilter: Arg.Any<QueryDeletedOption?>(),
            queryOrder: Arg.Any<BuildQueryOrder?>(),
            branchName: Arg.Any<string>(),
            buildIds: Arg.Any<IEnumerable<int>>(),
            repositoryId: Arg.Any<string>(),
            repositoryType: Arg.Any<string>(),
            userState: Arg.Any<object>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(latestBuild is not null ? new List<Build> { latestBuild } : new List<Build>());

        if (timeline is not null)
        {
            client.GetBuildTimelineAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(timeline);
        }

        return client;
    }

    [Fact]
    public async Task CollectAsync_NewBuild_NoExistingWorkItems_CreatesSignal()
    {
        var build = CreateBuild(100, BuildResult.Failed);
        var record = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(record);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync()
            .Returns([]);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(build, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
        Assert.Equal(100, pipelineSignal.TypedInfo.Build.Id);
        Assert.Equal(BuildResult.Failed, pipelineSignal.TypedInfo.Build.Result);
        Assert.Empty(pipelineSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_ExistingWorkItem_SameState_SkipsSignal()
    {
        var build = CreateBuild(100, BuildResult.Failed);
        var recordId = Guid.NewGuid();
        var record = CreateTimelineRecord(id: recordId, result: TaskResult.Failed);
        var timeline = CreateTimeline(record);

        var existingSignal = new AzureDevOpsPipelineSignal(
            "https://dev.azure.com/testorg",
            build,
            [ToInfoRecord(CreateTimelineRecord(id: recordId, result: TaskResult.Failed))])
        {
            WorkItemIds = ["wi-1"],
        };

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([
            new WorkItem
            {
                Id = "wi-1",
                SignalIds = [existingSignal.Id],
            }
        ]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(build, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_ExistingWorkItem_DifferentResult_CollectsWithPreservedWorkItemIds()
    {
        var existingBuild = CreateBuild(100, BuildResult.PartiallySucceeded);
        var existingRecord = CreateTimelineRecord(result: TaskResult.SucceededWithIssues);
        var existingSignal = new AzureDevOpsPipelineSignal("https://dev.azure.com/testorg", existingBuild, [ToInfoRecord(existingRecord)])
        {
            WorkItemIds = ["wi-1", "wi-2"],
        };

        var currentBuild = CreateBuild(100, BuildResult.Failed);
        var currentRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(currentRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([
            new WorkItem
            {
                Id = "wi-1",
                SignalIds = [existingSignal.Id],
            }
        ]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(currentBuild, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
        Assert.Equal(BuildResult.Failed, pipelineSignal.TypedInfo.Build.Result);
        Assert.Equal(["wi-1", "wi-2"], pipelineSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_ExistingWorkItem_DifferentTimeline_CollectsWithPreservedWorkItemIds()
    {
        var build = CreateBuild(100, BuildResult.Failed);
        var existingRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var existingSignal = new AzureDevOpsPipelineSignal("https://dev.azure.com/testorg", build, [ToInfoRecord(existingRecord)])
        {
            WorkItemIds = ["wi-5"],
        };

        var newRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(newRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([
            new WorkItem
            {
                Id = "wi-5",
                SignalIds = [existingSignal.Id],
            }
        ]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(build, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
        Assert.Equal(["wi-5"], pipelineSignal.WorkItemIds);
    }

    [Fact]
    public async Task CollectAsync_BuildResultNotInConfiguredStatus_SkipsSignal()
    {
        var build = CreateBuild(100, BuildResult.Succeeded);
        var record = CreateTimelineRecord(result: TaskResult.Succeeded);
        var timeline = CreateTimeline(record);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync()
            .Returns([]);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(build, timeline);
        var config = CreateConfig(1, ["refs/heads/main"], [BuildResult.Failed]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_NoTimelineRecords_SkipsSignal()
    {
        var build = CreateBuild(100, BuildResult.Failed);
        var timeline = CreateTimeline(CreateTimelineRecord(result: TaskResult.Succeeded));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync()
            .Returns([]);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(build, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_NoBuild_SkipsSignal()
    {
        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync()
            .Returns([]);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(null);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_MultipleBranches_CollectsFromEach()
    {
        var build1 = CreateBuild(100, BuildResult.Failed);
        var build2 = CreateBuild(200, BuildResult.Failed);
        var record = CreateTimelineRecord(result: TaskResult.Failed);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync()
            .Returns([]);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var client = Substitute.For<BuildHttpClient>(new Uri("https://dev.azure.com/test"), new VssCredentials());
        client.GetBuildsAsync(
            project: Arg.Any<string>(),
            definitions: Arg.Any<IEnumerable<int>>(),
            queues: Arg.Any<IEnumerable<int>>(),
            buildNumber: Arg.Any<string>(),
            minFinishTime: Arg.Any<DateTime?>(),
            maxFinishTime: Arg.Any<DateTime?>(),
            requestedFor: Arg.Any<string>(),
            reasonFilter: Arg.Any<BuildReason?>(),
            statusFilter: Arg.Any<BuildStatus?>(),
            resultFilter: Arg.Any<BuildResult?>(),
            tagFilters: Arg.Any<IEnumerable<string>>(),
            properties: Arg.Any<IEnumerable<string>>(),
            top: Arg.Any<int?>(),
            continuationToken: Arg.Any<string>(),
            maxBuildsPerDefinition: Arg.Any<int?>(),
            deletedFilter: Arg.Any<QueryDeletedOption?>(),
            queryOrder: Arg.Any<BuildQueryOrder?>(),
            branchName: "refs/heads/main",
            buildIds: Arg.Any<IEnumerable<int>>(),
            repositoryId: Arg.Any<string>(),
            repositoryType: Arg.Any<string>(),
            userState: Arg.Any<object>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new List<Build> { build1 });

        client.GetBuildsAsync(
            project: Arg.Any<string>(),
            definitions: Arg.Any<IEnumerable<int>>(),
            queues: Arg.Any<IEnumerable<int>>(),
            buildNumber: Arg.Any<string>(),
            minFinishTime: Arg.Any<DateTime?>(),
            maxFinishTime: Arg.Any<DateTime?>(),
            requestedFor: Arg.Any<string>(),
            reasonFilter: Arg.Any<BuildReason?>(),
            statusFilter: Arg.Any<BuildStatus?>(),
            resultFilter: Arg.Any<BuildResult?>(),
            tagFilters: Arg.Any<IEnumerable<string>>(),
            properties: Arg.Any<IEnumerable<string>>(),
            top: Arg.Any<int?>(),
            continuationToken: Arg.Any<string>(),
            maxBuildsPerDefinition: Arg.Any<int?>(),
            deletedFilter: Arg.Any<QueryDeletedOption?>(),
            queryOrder: Arg.Any<BuildQueryOrder?>(),
            branchName: "refs/heads/release/9.0",
            buildIds: Arg.Any<IEnumerable<int>>(),
            repositoryId: Arg.Any<string>(),
            repositoryType: Arg.Any<string>(),
            userState: Arg.Any<object>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new List<Build> { build2 });

        client.GetBuildTimelineAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(CreateTimeline(CreateTimelineRecord(result: TaskResult.Failed)));

        var config = CreateConfig(1, ["refs/heads/main", "refs/heads/release/9.0"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, client);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        Assert.Equal(2, signals.Count);
        var ids = signals.Cast<AzureDevOpsPipelineSignal>().Select(s => s.TypedInfo.Build.Id).OrderBy(x => x).ToList();
        Assert.Equal([100, 200], ids);
    }

    [Fact]
    public void HasSameTimelineRecords_ComparesIdAndResult()
    {
        var id = Guid.NewGuid();
        var existing = new List<TimelineRecord> { new() { Id = id, Result = TaskResult.Failed } };
        var sameCurrent = new List<TimelineRecord> { new() { Id = id, Result = TaskResult.Failed } };
        var differentResult = new List<TimelineRecord> { new() { Id = id, Result = TaskResult.Succeeded } };
        var differentCount = new List<TimelineRecord> { new() { Id = id, Result = TaskResult.Failed }, new() { Id = Guid.NewGuid(), Result = TaskResult.Failed } };

        Assert.True(AzureDevOpsSignalCollector.HasSameTimelineRecords(existing, sameCurrent));
        Assert.False(AzureDevOpsSignalCollector.HasSameTimelineRecords(existing, differentResult));
        Assert.False(AzureDevOpsSignalCollector.HasSameTimelineRecords(existing, differentCount));
        Assert.False(AzureDevOpsSignalCollector.HasSameTimelineRecords(null, sameCurrent));
        Assert.True(AzureDevOpsSignalCollector.HasSameTimelineRecords(new List<TimelineRecord>(), new List<TimelineRecord>()));
    }
}
