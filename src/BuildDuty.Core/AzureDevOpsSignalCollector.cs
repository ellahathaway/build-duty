using BuildDuty.Core.Models;
using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

public class AzureDevOpsSignalCollector : SignalCollector<AzureDevOpsConfig>
{
    private readonly ReleaseBranchResolver _branchResolver;
    private record OrganizationProjectContext(string OrganizationUrl, string ProjectName, BuildHttpClient BuildClient);

    public AzureDevOpsSignalCollector(
        AzureDevOpsConfig config,
        IRemoteTokenProvider tokenProvider,
        IStorageProvider storageProvider,
        ReleaseBranchResolver branchResolver)
        : base(config, tokenProvider, storageProvider)
    {
        _branchResolver = branchResolver;
    }

    protected override async Task<List<Signal>> CollectCoreAsync()
    {
        var pipelineSignals = (await StorageProvider.GetSignalsFromWorkItemsAsync())
            .Where(s => s.Type == SignalType.AzureDevOpsPipeline)
            .OfType<AzureDevOpsPipelineSignal>();

        var collectedSignals = new List<Signal>();
        foreach (var organization in Config.Organizations)
        {
            var buildClient = await GetBuildClientAsync(organization.Url);

            foreach (var project in organization.Projects)
            {
                var context = new OrganizationProjectContext(
                    organization.Url,
                    project.Name,
                    buildClient
                );

                var projectSignals = await CollectPipelineSignalsAsync(context, project.Pipelines, pipelineSignals);
                collectedSignals.AddRange(projectSignals);
            }
        }

        return collectedSignals;
    }

    protected virtual Task<BuildHttpClient> GetBuildClientAsync(string organizationUrl)
        => TokenProvider.GetAzureDevOpsBuildClientAsync(organizationUrl);

    private async Task<List<AzureDevOpsPipelineSignal>> CollectPipelineSignalsAsync(
        OrganizationProjectContext context,
        List<AzureDevOpsPipelineConfig> pipelines,
        IEnumerable<AzureDevOpsPipelineSignal> existingSignals
        )
    {
        var pipelineTasks = pipelines.Select(pipeline => CollectSinglePipelineSignalsAsync(context, pipeline, existingSignals));

        var results = await Task.WhenAll(pipelineTasks);
        return results.SelectMany(s => s).ToList();
    }

    private async Task<List<AzureDevOpsPipelineSignal>> CollectSinglePipelineSignalsAsync(
        OrganizationProjectContext context,
        AzureDevOpsPipelineConfig pipeline,
        IEnumerable<AzureDevOpsPipelineSignal> existingSignals
        )
    {
        var signals = new List<AzureDevOpsPipelineSignal>();
        var branches = await ResolveBranchesAsync(context, pipeline);

        foreach (var branch in branches)
        {
            var build = await GetLatestBuildAsync(context, pipeline.Id, branch, pipeline.Age);
            if (build == null || build.Result is not BuildResult buildResult || pipeline.Status?.Contains(buildResult) != true)
            {
                continue;
            }

            var timelineRecords = await GetTimelineRecordsAsync(context, build.Id, pipeline.TimelineFilters, pipeline.Status);
            if (timelineRecords.Count == 0)
            {
                continue;
            }

            var signal = new AzureDevOpsPipelineSignal(context.OrganizationUrl, build, timelineRecords);
            var existingSignal = existingSignals.FirstOrDefault(s => s.TypedInfo.Build.Id == build.Id);

            if (existingSignal == null)
            {
                signals.Add(signal);
                continue;
            }
            
            if (existingSignal.TypedInfo.Build.Result == buildResult
                && HasSameTimelineRecords(existingSignal.TypedInfo.TimelineRecords, timelineRecords))
            {
                continue;
            }

            signal.Id = existingSignal.Id; // Preserve the same ID for updates
            signal.WorkItemIds = existingSignal.WorkItemIds; // Preserve linked work items
            signals.Add(signal);
        }

        return signals;
    }

    internal static bool HasSameTimelineRecords(
        List<AzureDevOpsTimelineRecordInfo>? existing,
        List<AzureDevOpsTimelineRecordInfo> current)
    {
        if (existing is null || existing.Count != current.Count)
        {
            return false;
        }

        var existingSet = existing
            .Select(r => (r.Id, r.Result))
            .ToHashSet();

        return current.All(r => existingSet.Contains((r.Id, r.Result)));
    }

    internal static bool HasSameTimelineRecords(
        List<TimelineRecord>? existing,
        List<TimelineRecord> current)
    {
        if (existing is null || existing.Count != current.Count)
        {
            return false;
        }

        var existingSet = existing
            .Select(r => (r.Id, r.Result))
            .ToHashSet();

        return current.All(r => existingSet.Contains((r.Id, r.Result)));
    }

    private async Task<Build?> GetLatestBuildAsync(
        OrganizationProjectContext context,
        int definitionId,
        string branch,
        string? maxAge
        )
    {
        var builds = await context.BuildClient.GetBuildsAsync(
            project: context.ProjectName,
            definitions: [definitionId],
            branchName: branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ? branch : $"refs/heads/{branch}",
            queryOrder: BuildQueryOrder.FinishTimeDescending,
            top: 1);

        var build = builds?.FirstOrDefault();
        if (build == null)
        {
            return null;
        }

        var minFinishTime = GetMinFinishTime(maxAge);
        if (build.FinishTime is DateTime finishTime && minFinishTime.HasValue && finishTime < minFinishTime.Value)
        {
            return null;
        }

        return build;

        static DateTime? GetMinFinishTime(string? age)
        {
            if (age == null)
            {
                return null;
            }

            if (age.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(age[..^1], out var days))
            {
                return DateTime.UtcNow - TimeSpan.FromDays(days);
            }
            else if (age.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(age[..^1], out var hours))
            {
                return DateTime.UtcNow - TimeSpan.FromHours(hours);
            }
            else if (age.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(age[..^1], out var minutes))
            {
                return DateTime.UtcNow - TimeSpan.FromMinutes(minutes);
            }
            else
            {
                throw new FormatException($"Invalid max age format: '{age}'. Expected formats like '7d', '24h', or '30m'.");
            }
        }
    }

    private static async Task<List<AzureDevOpsTimelineRecordInfo>> GetTimelineRecordsAsync(
        OrganizationProjectContext context,
        int buildId,
        List<TimelineFilter>? filters,
        List<BuildResult>? status
        )
    {
        var timeline = await context.BuildClient.GetBuildTimelineAsync(context.ProjectName, buildId)
            ?? throw new InvalidOperationException($"Failed to retrieve timeline for build {buildId} in project {context.ProjectName}.");

        var allRecords = timeline.Records?.ToList() ?? [];
        var recordsById = allRecords.ToDictionary(r => r.Id, r => r);
        var allowedResults = status?
            .Select(result => GetTaskResult(result))
            .OfType<TaskResult>()
            .ToHashSet();

        var nonSuccessRecords = allRecords.Where(r =>
            r.Result is TaskResult result
            && allowedResults?.Contains(result) == true)
            .ToList();

        var filteredNonSuccessRecords = ApplyFilters(nonSuccessRecords, allRecords, recordsById, filters);
        var lowestRecords = GetLowestRecords(filteredNonSuccessRecords, recordsById);

        return lowestRecords
            .Select(r => ToRecordInfo(r, recordsById))
            .ToList();

        static List<TimelineRecord> ApplyFilters(
            List<TimelineRecord> failing,
            List<TimelineRecord> all,
            Dictionary<Guid, TimelineRecord> byId,
            List<TimelineFilter>? timelineFilters)
        {
            if (timelineFilters == null || timelineFilters.Count == 0)
            {
                return failing;
            }

            var anchors = all.Where(r => timelineFilters.Any(f => IsMatch(r, f))).ToList();
            if (anchors.Count == 0)
            {
                return [];
            }

            var anchorIds = anchors.Select(a => a.Id).ToHashSet();
            return failing.Where(r => IsSelfOrDescendantOfAny(r, anchorIds, byId)).ToList();

            static bool IsSelfOrDescendantOfAny(
                TimelineRecord record,
                HashSet<Guid> anchorIds,
                Dictionary<Guid, TimelineRecord> byId)
            {
                if (anchorIds.Contains(record.Id))
                {
                    return true;
                }

                var currentParentId = record.ParentId;
                while (currentParentId is Guid parentId && byId.TryGetValue(parentId, out var parent))
                {
                    if (anchorIds.Contains(parent.Id))
                    {
                        return true;
                    }

                    currentParentId = parent.ParentId;
                }

                return false;
            }
        }

        static List<TimelineRecord> GetLowestRecords(
            List<TimelineRecord> candidates,
            Dictionary<Guid, TimelineRecord> byId)
        {
            var candidateIds = candidates.Select(c => c.Id).ToHashSet();
            var nonLowestIds = new HashSet<Guid>();

            foreach (var candidate in candidates)
            {
                var currentParentId = candidate.ParentId;
                while (currentParentId is Guid parentId && byId.TryGetValue(parentId, out var parent))
                {
                    if (candidateIds.Contains(parent.Id))
                    {
                        nonLowestIds.Add(parent.Id);
                    }

                    currentParentId = parent.ParentId;
                }
            }

            return candidates
                .Where(candidate => !nonLowestIds.Contains(candidate.Id))
                .ToList();
        }

        static AzureDevOpsTimelineRecordInfo ToRecordInfo(
            TimelineRecord record,
            Dictionary<Guid, TimelineRecord> byId)
        {
            var parents = new List<AzureDevOpsTimelineParentInfo>();

            var currentParentId = record.ParentId;
            while (currentParentId is Guid parentId && byId.TryGetValue(parentId, out var parent))
            {
                parents.Add(new AzureDevOpsTimelineParentInfo(parent.Name, parent.RecordType, parent.Log?.Id));

                currentParentId = parent.ParentId;
            }

            parents.Reverse();

            return new AzureDevOpsTimelineRecordInfo(
                record.Id,
                record.Result,
                record.RecordType,
                record.Name,
                parents,
                record.Log?.Id);
        }

        static TaskResult? GetTaskResult(BuildResult? result)
        {
            return result switch
            {
                BuildResult.Succeeded => TaskResult.Succeeded,
                BuildResult.PartiallySucceeded => TaskResult.SucceededWithIssues,
                BuildResult.Failed => TaskResult.Failed,
                BuildResult.Canceled => TaskResult.Canceled,
                _ => null,
            };
        }

        static bool IsMatch(TimelineRecord record, TimelineFilter filter)
        {
            return record.RecordType.Equals(filter.Type.ToString(), StringComparison.OrdinalIgnoreCase) &&
                   filter.Names.Any(name => name.IsMatch(record.Name));
        }
    }

    private async Task<List<string>> ResolveBranchesAsync(
        OrganizationProjectContext context,
        AzureDevOpsPipelineConfig pipeline)
    {
        if (pipeline.Release is null)
        {
            return pipeline.Branches;
        }

        return await _branchResolver.ResolveAsync(
            context.OrganizationUrl,
            context.ProjectName,
            pipeline.Id,
            pipeline.Release.SupportPhases is { Count: > 0 }
                ? string.Join(',', pipeline.Release.SupportPhases)
                : null,
            pipeline.Release.MinVersion);
    }
}
