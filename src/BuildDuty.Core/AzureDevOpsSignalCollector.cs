using BuildDuty.Core.Models;
using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

public class AzureDevOpsSignalCollector(
    IBuildDutyConfigProvider configProvider,
    IRemoteTokenProvider tokenProvider,
    IStorageProvider storageProvider,
    ReleaseBranchResolver branchResolver)
    : SignalCollector<AzureDevOpsConfig>(configProvider, tokenProvider, storageProvider)
{
    protected override AzureDevOpsConfig ResolveConfig(BuildDutyConfig config) =>
        config.AzureDevOps ?? throw new InvalidOperationException("AzureDevOps config not found.");
    protected override async Task<List<ISignal>> CollectCoreAsync()
    {
        var pipelineSignals = (await StorageProvider.GetSignalsFromWorkItemsAsync())
            .Where(s => s.Type == SignalType.AzureDevOpsPipeline)
            .OfType<AzureDevOpsPipelineSignal>();

        var collectedSignals = new List<ISignal>();
        foreach (var organization in Config.Organizations)
        {
            var buildClient = await GetBuildClientAsync(organization.Url);

            foreach (var project in organization.Projects)
            {
                var context = new OrganizationProjectContext
                {
                    OrganizationUrl = organization.Url,
                    ProjectName = project.Name,
                    BuildClient = buildClient,
                };

                var projectSignals = await CollectPipelineSignalsAsync(context, project.Pipelines, pipelineSignals);
                collectedSignals.AddRange(projectSignals);
            }
        }

        return collectedSignals;
    }

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

            var existingSignal = existingSignals.FirstOrDefault(s => s.Info.Build.Id == build.Id);

            if (existingSignal == null)
            {
                signals.Add(new AzureDevOpsPipelineSignal(new AzureDevOpsPipelineInfo(build, timelineRecords)));
                continue;
            }
            
            if (existingSignal.Info.Build.Result == buildResult
                && HasSameTimelineRecords(existingSignal.Info.TimelineRecords, timelineRecords))
            {
                continue;
            }

            existingSignal.Info = new AzureDevOpsPipelineInfo(build, timelineRecords);
            signals.Add(existingSignal);
        }

        return signals;
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

    private static async Task<List<TimelineRecord>> GetTimelineRecordsAsync(
        OrganizationProjectContext context,
        int buildId,
        List<TimelineFilter>? filters,
        List<BuildResult>? status
        )
    {
        var timeline = await context.BuildClient.GetBuildTimelineAsync(context.ProjectName, buildId)
            ?? throw new InvalidOperationException($"Failed to retrieve timeline for build {buildId} in project {context.ProjectName}.");

        List<TimelineRecord> matchingRecords = new List<TimelineRecord>();
        foreach (var timelineRecord in timeline.Records)
        {
            // If the record doesn't have a result or its result doesn't match the configured status filters, skip it
            if (!timelineRecord.Result.HasValue || status?.Any(s => GetTaskResult(s) == timelineRecord.Result.Value) != true)
            {
                continue;
            }

            if (filters == null || filters.Count == 0 || filters.Any(f => IsMatch(timelineRecord, f)))
            {
                matchingRecords.Add(timelineRecord);
            }
        }

        return matchingRecords;

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

        bool IsMatch(TimelineRecord record, TimelineFilter filter)
        {
            return record.RecordType.Equals(filter.Type.ToString(), StringComparison.OrdinalIgnoreCase) &&
                   filter.Names.Any(name => name.IsMatch(record.Name));
        }
    }

    protected virtual async Task<BuildHttpClient> GetBuildClientAsync(string organizationUrl)
    {
        // Ensure trailing slash so Maestro's AzureDevOpsTokenProvider regex can extract the account name
        var repoUrl = organizationUrl.TrimEnd('/') + "/";
        var token = await TokenProvider.GetTokenForRepositoryAsync(repoUrl);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"No access token available for Azure DevOps organization '{organizationUrl}'.");
        }

        var credentials = new VssOAuthAccessTokenCredential(token);
        var connection = new VssConnection(new Uri(organizationUrl), credentials);
        return connection.GetClient<BuildHttpClient>();
    }

    private record OrganizationProjectContext
    {
        public required string OrganizationUrl { get; init; }
        public required string ProjectName { get; init; }
        public required BuildHttpClient BuildClient { get; init; }
    }

    private async Task<List<string>> ResolveBranchesAsync(
        OrganizationProjectContext context,
        AzureDevOpsPipelineConfig pipeline)
    {
        if (pipeline.Release is null)
        {
            return pipeline.Branches;
        }

        return await branchResolver.ResolveAsync(
            context.OrganizationUrl,
            context.ProjectName,
            pipeline.Id,
            pipeline.Release.SupportPhases is { Count: > 0 }
                ? string.Join(',', pipeline.Release.SupportPhases)
                : null,
            pipeline.Release.MinVersion);
    }
}
