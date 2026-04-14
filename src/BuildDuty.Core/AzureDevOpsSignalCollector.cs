using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

public class AzureDevOpsSignalCollector : SignalCollector<AzureDevOpsConfig>
{
    private readonly ReleaseBranchResolver _branchResolver;
    internal record OrganizationProjectContext(string OrganizationUrl, string ProjectName, VssConnection Connection, BuildHttpClient BuildClient);

    public AzureDevOpsSignalCollector(
        AzureDevOpsConfig config,
        IGeneralTokenProvider tokenProvider,
        IStorageProvider storageProvider,
        ReleaseBranchResolver branchResolver)
        : base(config, tokenProvider, storageProvider)
    {
        _branchResolver = branchResolver;
    }

    protected override async Task<List<Signal>> CollectCoreAsync()
    {
        var pipelineSignals = (await StorageProvider.GetAllSignalsAsync())
            .Where(s => s.Type == SignalType.AzureDevOpsPipeline)
            .OfType<AzureDevOpsPipelineSignal>();

        var collectedSignals = new List<Signal>();
        foreach (var organization in Config.Organizations)
        {
            var connection = await TokenProvider.GetAzureDevOpsConnectionAsync(organization.Url);
            var buildClient = await connection.GetClientAsync<BuildHttpClient>();

            foreach (var project in organization.Projects)
            {
                var context = new OrganizationProjectContext(
                    organization.Url,
                    project.Name,
                    connection,
                    buildClient
                );

                var projectSignals = await CollectPipelineSignalsAsync(context, project.Pipelines, pipelineSignals);
                collectedSignals.AddRange(projectSignals);
            }
        }

        return collectedSignals;
    }

    internal async Task<List<AzureDevOpsPipelineSignal>> CollectPipelineSignalsAsync(
        OrganizationProjectContext context,
        List<AzureDevOpsPipelineConfig> pipelines,
        IEnumerable<AzureDevOpsPipelineSignal> existingSignals
        )
    {
        foreach (var pipeline in pipelines)
        {
            if (pipeline.Status?.Contains(BuildResult.Succeeded) == true)
            {
                throw new InvalidOperationException(
                    $"Pipeline {pipeline.Id} has 'Succeeded' in its status list. " +
                    "Only non-successful results (Failed, PartiallySucceeded, Canceled) are supported. " +
                    "Successful builds are automatically tracked as recovery signals when a previously failing pipeline succeeds.");
            }

            if (pipeline.TimelineResults is not { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Pipeline {pipeline.Id} has an empty 'timelineResults' list. " +
                    "At least one TaskResult must be specified.");
            }

            if (pipeline.TimelineFilters is { Count: > 0 })
            {
                foreach (var filter in pipeline.TimelineFilters)
                {
                    if (filter.Status is not { Count: > 0 })
                    {
                        throw new InvalidOperationException(
                            $"Pipeline {pipeline.Id} has a timeline filter with an empty 'status' list. " +
                            "At least one TaskResult must be specified per filter.");
                    }
                }
            }
        }

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
            // Find existing signal for this pipeline+branch (not by build ID — the latest build may differ)
            var normalizedBranch = branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ? branch : $"refs/heads/{branch}";
            var existingSignal = existingSignals.FirstOrDefault(s =>
                s.TypedInfo.Build.DefinitionId == pipeline.Id
                && string.Equals(s.TypedInfo.Build.SourceBranch, normalizedBranch, StringComparison.OrdinalIgnoreCase));

            var build = await GetLatestBuildAsync(context, pipeline.Id, branch, pipeline.Age);

            if (build == null || build.Result is not BuildResult buildResult)
            {
                if (existingSignal != null)
                {
                    // No build found, but we had a previous signal
                    // Update the existing signal to have null build info so AI can see it's now "recovered" (no active failure)
                    var updatedSignal = new AzureDevOpsPipelineSignal(existingSignal.TypedInfo, existingSignal.Url);
                    updatedSignal.PreserveFrom(existingSignal);
                    signals.Add(updatedSignal);
                }
                continue;
            }

            if (existingSignal?.TypedInfo.Build.FinishTime == build.FinishTime)
            {
                // Build is the same as the existing signal's build, so skip (no new information for AI)
                continue;
            }

            if (!pipeline.Status.Contains(build.Result ?? BuildResult.None))
            {
                // Latest build is no longer a status we collect for (e.g. it succeeded).
                // If there's an existing signal tied to a work item, update it so AI can see it's now changed.
                if (existingSignal != null)
                {
                    var buildInfo = ToBuildInfo(build);
                    var pipelineInfo = new AzureDevOpsPipelineInfo(context.OrganizationUrl, build.Project?.Id ?? Guid.Empty, buildInfo, [], pipeline.Status);
                    var updatedSignal = new AzureDevOpsPipelineSignal(pipelineInfo, existingSignal.Url);
                    updatedSignal.Context = pipeline.Context;
                    updatedSignal.PreserveFrom(existingSignal);
                    signals.Add(updatedSignal);
                }
                continue;
            }

            var timelineRecords = await GetTimelineRecordsAsync(context, build.Id, pipeline.TimelineResults, pipeline.TimelineFilters);

            // If timeline filters are configured but no records matched, the failure is
            // in stages/jobs we don't monitor — skip this build.
            if (pipeline.TimelineFilters is { Count: > 0 } && timelineRecords.Count == 0)
            {
                continue;
            }

            var pInfo = new AzureDevOpsPipelineInfo(context.OrganizationUrl, build.Project?.Id ?? Guid.Empty, ToBuildInfo(build), timelineRecords, pipeline.Status);
            var buildUrl = new Uri($"{context.OrganizationUrl.TrimEnd('/')}/{build.Project?.Id ?? Guid.Empty}/_build/results?buildId={build.Id}");
            var signal = new AzureDevOpsPipelineSignal(pInfo, buildUrl);
            signal.Context = pipeline.Context;

            if (existingSignal == null)
            {
                signals.Add(signal);
                continue;
            }

            signal.PreserveFrom(existingSignal);
            signals.Add(signal);
        }

        return signals;
    }

    internal static AzureDevOpsBuildInfo ToBuildInfo(Build build) => new(
        build.Id,
        build.Result,
        build.Definition.Id,
        build.SourceBranch,
        build.FinishTime);

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
            statusFilter: BuildStatus.Completed,
            queryOrder: BuildQueryOrder.QueueTimeDescending,
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
        List<TaskResult> timelineResults,
        List<TimelineFilter>? filters
        )
    {
        var timeline = await context.BuildClient.GetBuildTimelineAsync(context.ProjectName, buildId)
            ?? throw new InvalidOperationException($"Failed to retrieve timeline for build {buildId} in project {context.ProjectName}.");

        var allRecords = timeline.Records?.ToList() ?? [];
        var recordsById = allRecords.ToDictionary(r => r.Id, r => r);

        // 1. Find matching records: filter by type/name/status from config,
        //    then walk down to the lowest (leaf) failures in the hierarchy.
        var matching = FindMatchingRecords(allRecords, recordsById, timelineResults, filters);
        var leaf = FindLeafRecords(matching, recordsById);

        // 2. Convert to info models with parent chains.
        return leaf.Select(r => ToRecordInfo(r, recordsById)).ToList();
    }

    /// <summary>
    /// Finds timeline records matching the configured filters.
    /// When no filters are configured, returns all non-successful records.
    /// When filters are configured, returns records that are descendants of
    /// (or are themselves) filter-matched anchors.
    /// </summary>
    private static List<TimelineRecord> FindMatchingRecords(
        List<TimelineRecord> allRecords,
        Dictionary<Guid, TimelineRecord> recordsById,
        List<TaskResult> timelineResults,
        List<TimelineFilter>? filters)
    {
        if (filters is null or { Count: 0 })
        {
            var allowedResults = timelineResults.ToHashSet();
            return allRecords
                .Where(r => r.Result.HasValue && allowedResults.Contains(r.Result.Value))
                .ToList();
        }

        // Find anchor records that match a filter by type + name + status.
        var anchorIds = allRecords
            .Where(r => filters.Any(f => MatchesFilter(r, f)))
            .Select(r => r.Id)
            .ToHashSet();

        if (anchorIds.Count == 0)
        {
            return [];
        }

        // Return all non-successful records that are an anchor or a descendant of an anchor.
        var allowedStatuses = filters.SelectMany(f => f.Status).ToHashSet();
        return allRecords
            .Where(r => r.Result.HasValue && allowedStatuses.Contains(r.Result.Value))
            .Where(r => IsOrDescendsFrom(r, anchorIds, recordsById))
            .ToList();
    }

    /// <summary>
    /// Returns true if the record matches type, name pattern, and status filter.
    /// </summary>
    private static bool MatchesFilter(TimelineRecord record, TimelineFilter filter)
    {
        if (!record.RecordType.Equals(filter.Type.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filter.Names.Any(name => name.IsMatch(record.Name))
            && (filter.Status.Count == 0 || filter.Status.Contains(record.Result.GetValueOrDefault()));
    }

    /// <summary>
    /// Returns true if the record's ID is in <paramref name="ancestorIds"/>,
    /// or if any of its parents are.
    /// </summary>
    private static bool IsOrDescendsFrom(
        TimelineRecord record,
        HashSet<Guid> ancestorIds,
        Dictionary<Guid, TimelineRecord> recordsById)
    {
        if (ancestorIds.Contains(record.Id))
        {
            return true;
        }

        var parentId = record.ParentId;
        while (parentId is Guid pid && recordsById.TryGetValue(pid, out var parent))
        {
            if (ancestorIds.Contains(parent.Id))
            {
                return true;
            }
            parentId = parent.ParentId;
        }

        return false;
    }

    /// <summary>
    /// Given a set of candidate records, removes any record that has a descendant
    /// also in the set — returning only the leaf-level (most specific) records.
    /// </summary>
    private static List<TimelineRecord> FindLeafRecords(
        List<TimelineRecord> candidates,
        Dictionary<Guid, TimelineRecord> recordsById)
    {
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var nonLeafIds = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            var parentId = candidate.ParentId;
            while (parentId is Guid pid && recordsById.TryGetValue(pid, out var parent))
            {
                if (candidateIds.Contains(parent.Id))
                {
                    nonLeafIds.Add(parent.Id);
                }
                parentId = parent.ParentId;
            }
        }

        return candidates
            .Where(c => !nonLeafIds.Contains(c.Id))
            .ToList();
    }

    private static AzureDevOpsTimelineRecordInfo ToRecordInfo(
        TimelineRecord record,
        Dictionary<Guid, TimelineRecord> recordsById)
    {
        var parents = new List<AzureDevOpsTimelineParentInfo>();

        var parentId = record.ParentId;
        while (parentId is Guid pid && recordsById.TryGetValue(pid, out var parent))
        {
            parents.Add(new AzureDevOpsTimelineParentInfo(parent.Name, parent.RecordType, parent.Log?.Id));
            parentId = parent.ParentId;
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

    private async Task<List<string>> ResolveBranchesAsync(
        OrganizationProjectContext context,
        AzureDevOpsPipelineConfig pipeline)
    {
        if (pipeline.Release is null)
        {
            return pipeline.Branches;
        }

        return await _branchResolver.ResolveAsync(
            context.Connection,
            context.ProjectName,
            pipeline.Id,
            pipeline.Release.SupportPhases is { Count: > 0 }
                ? string.Join(',', pipeline.Release.SupportPhases)
                : null,
            pipeline.Release.MinVersion);
    }
}
