using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

public class AzureDevOpsSignalCollector : SignalCollector<AzureDevOpsConfig>
{
    private readonly ReleaseBranchResolver _branchResolver;
    private readonly HashSet<string> _validExistingSignals = [];
    private readonly object _validExistingSignalsLock = new();
    protected internal record OrganizationProjectContext(string OrganizationUrl, string ProjectName, VssConnection Connection, BuildHttpClient BuildClient);

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
        var existingSignals = (await StorageProvider.GetUnresolvedSignalsAsync())
            .Where(s => s.Type == SignalType.AzureDevOpsPipeline)
            .OfType<AzureDevOpsPipelineSignal>()
            .ToList();

        var collectedSignals = new List<Signal>();

        foreach (var organization in Config.Organizations)
        {
            foreach (var project in organization.Projects)
            {
                var context = await CreateOrganizationProjectContextAsync(organization.Url, project.Name);

                var pipelineSignals = await CollectPipelineSignalsAsync(context, project.Pipelines, existingSignals);
                collectedSignals.AddRange(pipelineSignals);
            }
        }

        // Any existing signal not encountered during collection is out of scope.
        foreach (var existing in existingSignals)
        {
            if (!_validExistingSignals.Contains(existing.Id))
            {
                existing.AsOutOfScope();
                collectedSignals.Add(existing);
            }
        }

        return collectedSignals;
    }

    protected virtual async Task<OrganizationProjectContext> CreateOrganizationProjectContextAsync(string organizationUrl, string projectName)
    {
        var connection = await TokenProvider.GetAzureDevOpsConnectionAsync(organizationUrl);
        var buildClient = await connection.GetClientAsync<BuildHttpClient>();
        return new OrganizationProjectContext(organizationUrl, projectName, connection, buildClient);
    }

    internal async Task<List<AzureDevOpsPipelineSignal>> CollectPipelineSignalsAsync(
        OrganizationProjectContext context,
        List<AzureDevOpsPipelineConfig> pipelines,
        IEnumerable<AzureDevOpsPipelineSignal> existingSignals)
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

    internal async Task<List<AzureDevOpsPipelineSignal>> CollectSinglePipelineSignalsAsync(
        OrganizationProjectContext context,
        AzureDevOpsPipelineConfig pipeline,
        IEnumerable<AzureDevOpsPipelineSignal> existingSignals)
    {
        var signals = new List<AzureDevOpsPipelineSignal>();
        var branches = await ResolveBranchesAsync(context, pipeline);

        foreach (var branch in branches)
        {
            var existingSignal = existingSignals.FirstOrDefault(s =>
                s.TypedInfo.OrganizationUrl == context.OrganizationUrl &&
                s.TypedInfo.ProjectName == context.ProjectName &&
                s.TypedInfo.PipelineId == pipeline.Id &&
                s.TypedInfo.Build?.SourceBranch == branch);

            if (existingSignal != null)
            {
                lock (_validExistingSignalsLock)
                {
                    _validExistingSignals.Add(existingSignal.Id);
                }
            }

            var build = await GetLatestBuildAsync(context, pipeline.Id, branch, pipeline.Age);

            // Build is invalid
            if (build == null || build.Result is not BuildResult buildResult)
            {
                if (existingSignal != null)
                {
                    // Existing signal is no longer valid
                    existingSignal.AsNotFound();
                    existingSignal.Context = pipeline.Context;
                    signals.Add(existingSignal);
                }
                continue;
            }

            // Build is the same as the existing signal's build
            if (existingSignal?.TypedInfo.Build?.FinishTime == build.FinishTime)
            {
                continue;
            }

            // Build is not in the list of statuses we collect
            if (!pipeline.Status.Contains(build.Result ?? BuildResult.None))
            {
                // Existing signal is resolved
                if (existingSignal != null)
                {
                    existingSignal.AsResolved(build, pipeline.Context);
                    signals.Add(existingSignal);
                }
                continue;
            }

            var timelineRecords = await GetTimelineRecordsAsync(context, build.Id, pipeline.TimelineResults, pipeline.TimelineFilters);

            // Timeline records don't match the configured filters
            if (pipeline.TimelineFilters is { Count: > 0 } && timelineRecords.Count == 0)
            {
                // Existing signal is out of scope due to no matching timeline records
                if (existingSignal != null)
                {
                    existingSignal.AsOutOfScope();
                    existingSignal.Context = pipeline.Context;
                    signals.Add(existingSignal);
                }
                continue;
            }

            // Timeline records match the configured filters and there is an existing signal
            if (existingSignal != null)
            {
                existingSignal.AsUpdated(build, timelineRecords, pipeline.Context);
                signals.Add(existingSignal);
                continue;
            }

            // Create a new signal for the current build
            var buildInfo = new AzureDevOpsBuildInfo(build.Id, build.Result, build.Definition.Id, build.SourceBranch, build.FinishTime);
            var pInfo = new AzureDevOpsPipelineInfo(context.OrganizationUrl, context.ProjectName, pipeline.Id, buildInfo, timelineRecords);
            var buildUrl = new Uri($"{context.OrganizationUrl.TrimEnd('/')}/{context.ProjectName}/_build/results?buildId={build.Id}");
            var signal = new AzureDevOpsPipelineSignal(pInfo, buildUrl);
            signals.Add(signal);
        }

        return signals;
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
        var branches = pipeline.Release is null
            ? pipeline.Branches
            : await _branchResolver.ResolveAsync(
                context.Connection,
                context.ProjectName,
                pipeline.Id,
                pipeline.Release.SupportPhases is { Count: > 0 }
                    ? string.Join(',', pipeline.Release.SupportPhases)
                    : null,
                pipeline.Release.MinVersion);

        return branches
            .Select(b => b.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ? b : $"refs/heads/{b}")
            .ToList();
    }

}
