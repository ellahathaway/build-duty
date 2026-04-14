using System.Text.RegularExpressions;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;
using Xunit;

namespace BuildDuty.Tests;

public class AzureDevOpsPipelineSignalTests
{
    private static Build CreateBuild(int id, BuildResult result, int definitionId = 1, string sourceBranch = "refs/heads/main", DateTime? finishTime = null) => new()
    {
        Id = id,
        Result = result,
        Definition = new DefinitionReference { Id = definitionId },
        SourceBranch = sourceBranch,
        FinishTime = finishTime,
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

    private static AzureDevOpsPipelineInfo ToPipelineInfo(
        string orgUrl, Build build, List<AzureDevOpsTimelineRecordInfo> records,
        List<BuildResult>? monitoredStatuses = null) =>
        new(orgUrl, build.Project?.Id ?? Guid.Empty,
            AzureDevOpsSignalCollector.ToBuildInfo(build), records,
            monitoredStatuses ?? [BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled]);

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
            : base(config, CreateMockTokenProvider(), storageProvider, new ReleaseBranchResolver())
        {
            _buildClient = buildClient;
        }

        protected override async Task<List<Signal>> CollectCoreAsync()
        {
            var pipelineSignals = (await StorageProvider.GetAllSignalsAsync())
                .Where(s => s.Type == SignalType.AzureDevOpsPipeline)
                .OfType<AzureDevOpsPipelineSignal>();

            var collectedSignals = new List<Signal>();
            foreach (var organization in Config.Organizations)
            {
                foreach (var project in organization.Projects)
                {
                    var context = new OrganizationProjectContext(
                        organization.Url,
                        project.Name,
                        null!,
                        _buildClient
                    );

                    var projectSignals = await CollectPipelineSignalsAsync(context, project.Pipelines, pipelineSignals);
                    collectedSignals.AddRange(projectSignals);
                }
            }

            return collectedSignals;
        }

        private static IRemoteTokenProvider CreateMockTokenProvider()
        {
            var mock = Substitute.For<IRemoteTokenProvider>();
            mock.GetTokenForRepositoryAsync(Arg.Any<string>()).Returns("fake-token");
            return mock;
        }
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
        var build = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 5));
        var record = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(record);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
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
    }

    [Fact]
    public async Task CollectAsync_ExistingWorkItem_SameState_SkipsSignal()
    {
        var build = CreateBuild(100, BuildResult.Failed);
        var recordId = Guid.NewGuid();
        var record = CreateTimelineRecord(id: recordId, result: TaskResult.Failed);
        var timeline = CreateTimeline(record);

        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", build, [ToInfoRecord(CreateTimelineRecord(id: recordId, result: TaskResult.Failed))]), new Uri("https://dev.azure.com/testorg"));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
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
    public async Task CollectAsync_SameFinishTime_SkipsSignal()
    {
        var finishTime = new DateTime(2026, 4, 5, 12, 0, 0);
        var build = CreateBuild(100, BuildResult.Failed, finishTime: finishTime);
        var record = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(record);

        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", build, [ToInfoRecord(record)]), new Uri("https://dev.azure.com/testorg"));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
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
    public async Task CollectAsync_ExistingWorkItem_DifferentResult_Collects()
    {
        var existingBuild = CreateBuild(100, BuildResult.PartiallySucceeded, finishTime: new DateTime(2026, 4, 1));
        var existingRecord = CreateTimelineRecord(result: TaskResult.SucceededWithIssues);
        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", existingBuild, [ToInfoRecord(existingRecord)]), new Uri("https://dev.azure.com/testorg"));

        var currentBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var currentRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(currentRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
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
    }

    [Fact]
    public async Task CollectAsync_ExistingWorkItem_DifferentTimeline_Collects()
    {
        var build = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", build, [ToInfoRecord(existingRecord)]), new Uri("https://dev.azure.com/testorg"));

        var newRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(newRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-5", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2)), timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
    }

    [Fact]
    public async Task CollectAsync_BuildResultNotInConfiguredStatus_SkipsSignal()
    {
        var build = CreateBuild(100, BuildResult.Succeeded);
        var record = CreateTimelineRecord(result: TaskResult.Succeeded);
        var timeline = CreateTimeline(record);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
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
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
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
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
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
        var build1 = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 5));
        var build2 = CreateBuild(200, BuildResult.Failed, finishTime: new DateTime(2026, 4, 6));
        var record = CreateTimelineRecord(result: TaskResult.Failed);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
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
    public async Task CollectAsync_BuildNowPassing_ExistingSignal_UpdatesWithPassingBuild()
    {
        // Existing signal was for a failed build
        var failedBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", failedBuild, [ToInfoRecord(CreateTimelineRecord(result: TaskResult.Failed))]), new Uri("https://dev.azure.com/testorg"));

        // Latest build is now succeeding
        var passingBuild = CreateBuild(101, BuildResult.Succeeded, finishTime: new DateTime(2026, 4, 2));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(passingBuild);
        var config = CreateConfig(1, ["refs/heads/main"], [BuildResult.Failed]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
        Assert.Equal(existingSignal.Id, pipelineSignal.Id);
        Assert.Equal(BuildResult.Succeeded, pipelineSignal.TypedInfo.Build.Result);
        Assert.Equal(101, pipelineSignal.TypedInfo.Build.Id);
        Assert.Empty(pipelineSignal.TypedInfo.TimelineRecords);
    }

    [Fact]
    public async Task CollectAsync_BuildNowPassing_NoExistingSignal_Skips()
    {
        // Latest build is succeeding and there's no existing signal
        var passingBuild = CreateBuild(101, BuildResult.Succeeded);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns(Array.Empty<WorkItem>());
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(passingBuild);
        var config = CreateConfig(1, ["refs/heads/main"], [BuildResult.Failed]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var savedCount = storageProvider.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync));

        Assert.Equal(0, savedCount);
    }

    [Fact]
    public async Task CollectAsync_NewerFailedBuild_DifferentBuildId_UpdatesExisting()
    {
        // Existing signal pointed to build 100, but latest build for same pipeline+branch is 101
        var oldBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingSignal = new AzureDevOpsPipelineSignal(ToPipelineInfo("https://dev.azure.com/testorg", oldBuild, [ToInfoRecord(CreateTimelineRecord(result: TaskResult.Failed))]), new Uri("https://dev.azure.com/testorg"));

        var newBuild = CreateBuild(101, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var newRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var timeline = CreateTimeline(newRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(newBuild, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);
        Assert.Equal(existingSignal.Id, pipelineSignal.Id);
        Assert.Equal(101, pipelineSignal.TypedInfo.Build.Id);
    }
}

