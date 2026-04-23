using System.Text.RegularExpressions;
using BuildDuty.Core;
using BuildDuty.Core.Models;
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
        string orgUrl, Build build, List<AzureDevOpsTimelineRecordInfo> records) =>
        new(orgUrl, build.Project?.Name ?? "TestProject", build.Definition?.Id ?? 0,
            new AzureDevOpsBuildInfo(build.Id, build.Result, build.Definition?.Id ?? 0, build.SourceBranch, build.FinishTime), records);

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

        protected override Task<OrganizationProjectContext> CreateOrganizationProjectContextAsync(string organizationUrl, string projectName)
        {
            return Task.FromResult(new OrganizationProjectContext(organizationUrl, projectName, null!, _buildClient));
        }

        private static IGeneralTokenProvider CreateMockTokenProvider()
        {
            var mock = Substitute.For<IGeneralTokenProvider>();
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
        Assert.Equal(100, pipelineSignal.TypedInfo.Build!.Id);
        Assert.Equal(BuildResult.Failed, pipelineSignal.TypedInfo.Build!.Result);
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
        Assert.Equal(BuildResult.Failed, pipelineSignal.TypedInfo.Build!.Result);
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
        var ids = signals.Cast<AzureDevOpsPipelineSignal>().Select(s => s.TypedInfo.Build!.Id).OrderBy(x => x).ToList();
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
        Assert.Equal(BuildResult.Succeeded, pipelineSignal.TypedInfo.Build!.Result);
        Assert.Equal(101, pipelineSignal.TypedInfo.Build!.Id);
        Assert.Empty(pipelineSignal.TypedInfo.TimelineRecords!);
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
        Assert.Equal(101, pipelineSignal.TypedInfo.Build!.Id);
    }

    [Fact]
    public async Task CollectAsync_RerunBuild_NewerQueuedBuildIsCollected()
    {
        // Scenario: Build 100 was a rerun of an old pipeline run — it was queued on April 1
        // but rerun and finished on April 14. Build 200 is the genuinely newer build — queued
        // on April 13 and finished on April 13. With QueueTimeDescending ordering, the API
        // returns build 200 (most recently queued), not the rerun that finished later.
        var rerunBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 14, 5, 0, 0));
        var newerBuild = CreateBuild(200, BuildResult.Failed, finishTime: new DateTime(2026, 4, 13, 18, 15, 0));

        var rerunRecord = CreateTimelineRecord(result: TaskResult.Failed);
        var newerRecord = CreateTimelineRecord(result: TaskResult.Failed);

        // Existing signal was collected from the rerun build (simulating the old FinishTimeDescending behavior)
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", rerunBuild, [ToInfoRecord(rerunRecord)]),
            new Uri("https://dev.azure.com/testorg"));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        // With QueueTimeDescending, the API returns the newer-queued build, not the rerun
        var buildClient = CreateMockBuildClient(newerBuild, CreateTimeline(newerRecord));
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var signals = storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => (Signal)call.GetArguments()[0]!)
            .ToList();

        var signal = Assert.Single(signals);
        var pipelineSignal = Assert.IsType<AzureDevOpsPipelineSignal>(signal);

        // The collected signal should have the newer-queued build, not the rerun
        Assert.Equal(200, pipelineSignal.TypedInfo.Build!.Id);
        // Signal identity should be preserved from the existing signal
        Assert.Equal(existingSignal.Id, pipelineSignal.Id);
    }

    /// <summary>
    /// Helper to create an existing signal, link it to a work item, and set up storage mocks.
    /// Returns the existing signal and configured storage provider.
    /// </summary>
    private static (AzureDevOpsPipelineSignal Signal, IStorageProvider Storage) CreateExistingSignalWithWorkItem(
        string orgUrl = "https://dev.azure.com/testorg",
        string sourceBranch = "refs/heads/main",
        int definitionId = 1,
        BuildResult result = BuildResult.Failed)
    {
        var build = CreateBuild(100, result, definitionId: definitionId, sourceBranch: sourceBranch, finishTime: new DateTime(2026, 4, 1));
        var record = CreateTimelineRecord(result: TaskResult.Failed);
        var signal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo(orgUrl, build, [ToInfoRecord(record)]),
            new Uri(orgUrl));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(signal.Id, [])] }]);
        storageProvider.GetSignalAsync(signal.Id).Returns(signal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        return (signal, storageProvider);
    }

    private static List<AzureDevOpsPipelineSignal> GetSavedPipelineSignals(IStorageProvider storageProvider) =>
        storageProvider.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IStorageProvider.SaveSignalAsync))
            .Select(call => call.GetArguments()[0])
            .OfType<AzureDevOpsPipelineSignal>()
            .ToList();

    private static void AssertResolvedSignal(AzureDevOpsPipelineSignal signal, string expectedId)
    {
        Assert.Equal(expectedId, signal.Id);
        Assert.Empty(signal.TypedInfo.TimelineRecords!);
        Assert.Equal(SignalCollectionReason.Resolved, signal.CollectionReason);
    }

    private static void AssertOutOfScopeSignal(AzureDevOpsPipelineSignal signal, string expectedId)
    {
        Assert.Equal(expectedId, signal.Id);
        Assert.Equal(SignalCollectionReason.OutOfScope, signal.CollectionReason);
    }

    [Fact]
    public async Task CollectAsync_OrgRemovedFromConfig_ExistingSignal_MarkedOutOfScope()
    {
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem(orgUrl: "https://dev.azure.com/removedorg");
        var buildClient = CreateMockBuildClient(null);

        // Config has a different org than the existing signal
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertOutOfScopeSignal(signal, existingSignal.Id);
    }

    [Fact]
    public async Task CollectAsync_ProjectRemovedFromConfig_ExistingSignal_MarkedOutOfScope()
    {
        // Existing signal has project "TestProject" but we'll create a config with a different project name
        var build = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var record = CreateTimelineRecord(result: TaskResult.Failed);
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", build, [ToInfoRecord(record)]),
            new Uri("https://dev.azure.com/testorg"));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(null);

        // Config has project "OtherProject" — existing signal's "TestProject" no longer matches
        var config = new AzureDevOpsConfig
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
                            Name = "OtherProject",
                            Pipelines =
                            [
                                new AzureDevOpsPipelineConfig
                                {
                                    Id = 1,
                                    Name = "TestPipeline",
                                    Branches = ["refs/heads/main"],
                                    Status = [BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled],
                                    TimelineFilters = [new TimelineFilter { Type = TimelineRecordType.Job, Names = [new Regex(".*")] }],
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertOutOfScopeSignal(signal, existingSignal.Id);
    }

    [Fact]
    public async Task CollectAsync_PipelineRemovedFromConfig_ExistingSignal_MarkedOutOfScope()
    {
        // Existing signal is for pipeline 1, but config now only has pipeline 99
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem(definitionId: 1);
        var buildClient = CreateMockBuildClient(null);

        var config = CreateConfig(99, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertOutOfScopeSignal(signal, existingSignal.Id);
    }

    [Fact]
    public async Task CollectAsync_BranchRemovedFromConfig_ExistingSignal_MarkedOutOfScope()
    {
        // Existing signal is for refs/heads/main, but config now only has refs/heads/release/9.0
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem(sourceBranch: "refs/heads/main");
        var buildClient = CreateMockBuildClient(null);

        var config = CreateConfig(1, ["refs/heads/release/9.0"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertOutOfScopeSignal(signal, existingSignal.Id);
    }

    [Fact]
    public async Task CollectAsync_TimelineFilterChanged_ExistingSignal_MarkedOutOfScope()
    {
        // Existing signal has a "Build" job record, but config's timeline filter now only matches "Deploy"
        var build = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingRecord = CreateTimelineRecord(result: TaskResult.Failed, name: "Build");
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", build, [ToInfoRecord(existingRecord)]),
            new Uri("https://dev.azure.com/testorg"));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        // New build still fails, but the timeline only has a "Build" job — and the filter now requires "Deploy"
        var newBuild = CreateBuild(101, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var newRecord = CreateTimelineRecord(result: TaskResult.Failed, name: "Build");
        var timeline = CreateTimeline(newRecord);
        var buildClient = CreateMockBuildClient(newBuild, timeline);

        var config = new AzureDevOpsConfig
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
                                    Id = 1,
                                    Name = "TestPipeline",
                                    Branches = ["refs/heads/main"],
                                    Status = [BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled],
                                    TimelineFilters = [new TimelineFilter { Type = TimelineRecordType.Job, Names = [new Regex("^Deploy$")] }],
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertOutOfScopeSignal(signal, existingSignal.Id);
    }

    [Fact]
    public async Task CollectAsync_BuildResultNoLongerMonitored_ExistingSignal_MarkedOutOfScope()
    {
        // Existing signal was for a Failed build, config previously monitored Failed.
        // Now config only monitors PartiallySucceeded — and the latest build is still Failed.
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem(result: BuildResult.Failed);

        var latestBuild = CreateBuild(101, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var buildClient = CreateMockBuildClient(latestBuild);

        // Config now only monitors PartiallySucceeded — Failed is no longer tracked
        var config = CreateConfig(1, ["refs/heads/main"], [BuildResult.PartiallySucceeded]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        AssertResolvedSignal(signal, existingSignal.Id);
    }

    // ========== CollectionReason Tests ==========

    [Fact]
    public async Task CollectAsync_NewSignal_HasCollectionReasonNew()
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

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(SignalCollectionReason.New, signal.CollectionReason);
    }

    [Fact]
    public async Task CollectAsync_UpdatedSignal_HasCollectionReasonUpdated()
    {
        var oldBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", oldBuild, [ToInfoRecord(CreateTimelineRecord(result: TaskResult.Failed))]),
            new Uri("https://dev.azure.com/testorg"));

        var newBuild = CreateBuild(101, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var timeline = CreateTimeline(CreateTimelineRecord(result: TaskResult.Failed));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(newBuild, timeline);
        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(existingSignal.Id, signal.Id);
        Assert.Equal(SignalCollectionReason.Updated, signal.CollectionReason);
        Assert.Equal(101, signal.TypedInfo.Build!.Id);
    }

    [Fact]
    public async Task CollectAsync_ResolvedSignal_HasCollectionReasonResolved()
    {
        var failedBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", failedBuild, [ToInfoRecord(CreateTimelineRecord(result: TaskResult.Failed))]),
            new Uri("https://dev.azure.com/testorg"));

        var passingBuild = CreateBuild(101, BuildResult.Succeeded, finishTime: new DateTime(2026, 4, 2));

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(passingBuild);
        var config = CreateConfig(1, ["refs/heads/main"], [BuildResult.Failed]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(existingSignal.Id, signal.Id);
        Assert.Equal(SignalCollectionReason.Resolved, signal.CollectionReason);
        Assert.Equal(101, signal.TypedInfo.Build!.Id);
        Assert.Empty(signal.TypedInfo.TimelineRecords!);
    }

    [Fact]
    public async Task CollectAsync_NotFoundSignal_HasCollectionReasonNotFound()
    {
        // Existing signal but no build found (e.g. builds aged out)
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem();
        var buildClient = CreateMockBuildClient(null);

        var config = CreateConfig(1, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(existingSignal.Id, signal.Id);
        Assert.Equal(SignalCollectionReason.NotFound, signal.CollectionReason);
    }

    [Fact]
    public async Task CollectAsync_OutOfScopeSignal_ConfigDrift_HasCollectionReasonOutOfScope()
    {
        // Existing signal's pipeline was removed from config
        var (existingSignal, storageProvider) = CreateExistingSignalWithWorkItem(definitionId: 1);
        var buildClient = CreateMockBuildClient(null);

        // Config has pipeline 99, not pipeline 1
        var config = CreateConfig(99, ["refs/heads/main"]);
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(existingSignal.Id, signal.Id);
        Assert.Equal(SignalCollectionReason.OutOfScope, signal.CollectionReason);
    }

    [Fact]
    public async Task CollectAsync_OutOfScopeSignal_TimelineNoMatch_HasCollectionReasonOutOfScope()
    {
        // Build still fails but timeline records don't match the configured filters
        var oldBuild = CreateBuild(100, BuildResult.Failed, finishTime: new DateTime(2026, 4, 1));
        var existingSignal = new AzureDevOpsPipelineSignal(
            ToPipelineInfo("https://dev.azure.com/testorg", oldBuild, [ToInfoRecord(CreateTimelineRecord(result: TaskResult.Failed, name: "Build"))]),
            new Uri("https://dev.azure.com/testorg"));

        var newBuild = CreateBuild(101, BuildResult.Failed, finishTime: new DateTime(2026, 4, 2));
        var newRecord = CreateTimelineRecord(result: TaskResult.Failed, name: "Build");
        var timeline = CreateTimeline(newRecord);

        var storageProvider = Substitute.For<IStorageProvider>();
        storageProvider.GetWorkItemsAsync().Returns([new WorkItem { Id = "wi-1", LinkedAnalyses = [new LinkedAnalysis(existingSignal.Id, [])] }]);
        storageProvider.GetSignalAsync(existingSignal.Id).Returns(existingSignal);
        storageProvider.SaveSignalAsync(Arg.Any<Signal>()).Returns(Task.CompletedTask);

        var buildClient = CreateMockBuildClient(newBuild, timeline);

        // Timeline filter requires "Deploy" but build only has "Build"
        var config = new AzureDevOpsConfig
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
                                    Id = 1,
                                    Name = "TestPipeline",
                                    Branches = ["refs/heads/main"],
                                    Status = [BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled],
                                    TimelineFilters = [new TimelineFilter { Type = TimelineRecordType.Job, Names = [new Regex("^Deploy$")] }],
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var collector = new TestableAzureDevOpsCollector(config, storageProvider, buildClient);

        await collector.CollectAsync();

        var saved = GetSavedPipelineSignals(storageProvider);
        var signal = Assert.Single(saved);
        Assert.Equal(existingSignal.Id, signal.Id);
        Assert.Equal(SignalCollectionReason.OutOfScope, signal.CollectionReason);
    }
}

