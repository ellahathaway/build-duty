using System.Text.RegularExpressions;
using BuildDuty.Signals.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Signals.Collection;

/// <summary>
/// Collects pipeline signals from Azure DevOps.
/// </summary>
internal sealed class AzureDevOpsSignalCollector : ISignalCollector
{
    private readonly AzureDevOpsConfig _config;
    private readonly ITokenProvider _tokenProvider;
    private readonly ReleaseBranchResolver _branchResolver;
    private readonly ILogger _logger;

    internal AzureDevOpsSignalCollector(AzureDevOpsConfig config, ITokenProvider tokenProvider, ILogger logger, ReleaseBranchResolver? branchResolver = null)
    {
        _config = config;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _branchResolver = branchResolver ?? new ReleaseBranchResolver();
    }

    public async Task<IReadOnlyList<Signal>> CollectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collecting Azure DevOps signals");

        var orgTasks = _config.Organizations.Select(async organization =>
        {
            var connection = await CreateConnectionAsync(organization.Url);
            var buildClient = connection.GetClient<BuildHttpClient>();

            var projectTasks = organization.Projects
                .Select(project => CollectProjectSignalsAsync(
                    organization.Url,
                    connection,
                    buildClient,
                    project,
                    cancellationToken));

            var projectSignals = await Task.WhenAll(projectTasks);
            return projectSignals.SelectMany(s => s);
        });

        var orgSignals = (await Task.WhenAll(orgTasks)).SelectMany(s => s).ToList();
        _logger.LogInformation("Collected {Count} Azure DevOps signals", orgSignals.Count);
        return orgSignals;
    }

    private async Task<List<Signal>> CollectProjectSignalsAsync(
        string orgUrl,
        VssConnection connection,
        BuildHttpClient buildClient,
        AzureDevOpsProjectConfig project,
        CancellationToken cancellationToken)
    {
        var signals = new List<Signal>();

        var pipelineTasks = project.Pipelines
            .Select(pipeline => CollectPipelineSignalsAsync(orgUrl, project.Name, pipeline, buildClient, connection, cancellationToken));
        var pipelineSignals = await Task.WhenAll(pipelineTasks);

        foreach (var batch in pipelineSignals)
        {
            signals.AddRange(batch);
        }

        return signals;
    }

    private async Task<List<AzureDevOpsPipelineSignal>> CollectPipelineSignalsAsync(
        string orgUrl,
        string projectName,
        AzureDevOpsPipelineConfig pipeline,
        BuildHttpClient buildClient,
        VssConnection connection,
        CancellationToken cancellationToken)
    {
        var signals = new List<AzureDevOpsPipelineSignal>();
        var branches = pipeline.Branches.ToList();

        // If release branch config is set, resolve branches dynamically
        if (branches.Count == 0 && pipeline.Release is not null)
        {
            try
            {
                branches = await _branchResolver.ResolveAsync(
                    connection, projectName, pipeline.Id, pipeline.Release);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving release branches for pipeline {PipelineName}", pipeline.Name);
                return signals;
            }
        }

        foreach (var branch in branches)
        {
            try
            {
                var builds = await buildClient.GetBuildsAsync(
                    project: projectName,
                    definitions: [pipeline.Id],
                    branchName: $"refs/heads/{branch}",
                    queryOrder: BuildQueryOrder.FinishTimeDescending,
                    top: 1,
                    cancellationToken: cancellationToken);

                var build = builds.FirstOrDefault();
                if (build is null)
                {
                    continue;
                }

                // Check age filter
                if (!string.IsNullOrEmpty(pipeline.Age) && build.FinishTime.HasValue)
                {
                    var maxAge = ParseAge(pipeline.Age);
                    if (DateTime.UtcNow - build.FinishTime.Value > maxAge)
                    {
                        continue;
                    }
                }

                // Only collect signals for configured failure statuses
                if (build.Result is null || !pipeline.Status.Contains(build.Result.Value))
                {
                    continue;
                }

                var timelineRecords = await CollectTimelineRecordsAsync(
                    buildClient, projectName, build.Id, pipeline, cancellationToken);

                var signal = new AzureDevOpsPipelineSignal
                {
                    OrganizationUrl = orgUrl,
                    ProjectName = projectName,
                    PipelineId = pipeline.Id,
                    Url = $"{orgUrl}/{projectName}/_build/results?buildId={build.Id}",
                    Context = pipeline.Context,
                    Build = new AzureDevOpsBuildInfo
                    {
                        Id = build.Id,
                        Result = build.Result.Value,
                        DefinitionId = build.Definition.Id,
                        SourceBranch = build.SourceBranch,
                        FinishTime = build.FinishTime
                    },
                    TimelineRecords = timelineRecords
                };

                signals.Add(signal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting pipeline {PipelineName} branch {Branch}", pipeline.Name, branch);
            }
        }

        return signals;
    }

    private async Task<List<AzureDevOpsTimelineRecordInfo>> CollectTimelineRecordsAsync(
        BuildHttpClient buildClient,
        string projectName,
        int buildId,
        AzureDevOpsPipelineConfig pipeline,
        CancellationToken cancellationToken)
    {
        var records = new List<AzureDevOpsTimelineRecordInfo>();

        try
        {
            var timeline = await buildClient.GetBuildTimelineAsync(projectName, buildId, cancellationToken: cancellationToken);
            if (timeline?.Records is null)
            {
                return records;
            }

            foreach (var record in timeline.Records)
            {
                if (record.Result is null)
                {
                    continue;
                }

                if (pipeline.TimelineFilters is { Count: > 0 })
                {
                    if (!MatchesTimelineFilter(record, pipeline.TimelineFilters))
                    {
                        continue;
                    }
                }
                else if (!pipeline.TimelineResults.Contains(record.Result.Value))
                {
                    continue;
                }

                var parents = BuildParentChain(record, timeline.Records);

                records.Add(new AzureDevOpsTimelineRecordInfo
                {
                    Id = record.Id.ToString(),
                    Result = record.Result.Value,
                    RecordType = record.RecordType,
                    Name = record.Name,
                    LogId = record.Log?.Id ?? 0,
                    Parents = parents
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching timeline for build {BuildId}", buildId);
        }

        return records;
    }

    private static bool MatchesTimelineFilter(
        TimelineRecord record,
        List<TimelineFilter> filters)
    {
        foreach (var filter in filters)
        {
            var recordType = Enum.TryParse<Configuration.TimelineRecordType>(record.RecordType, true, out var rt) ? rt : (Configuration.TimelineRecordType?)null;
            if (recordType != filter.Type)
            {
                continue;
            }

            if (record.Result is null || !filter.Status.Contains(record.Result.Value))
            {
                continue;
            }

            foreach (var namePattern in filter.Names)
            {
                if (Regex.IsMatch(record.Name, namePattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<AzureDevOpsTimelineParentInfo> BuildParentChain(
        TimelineRecord record,
        IList<TimelineRecord> allRecords)
    {
        var parents = new List<AzureDevOpsTimelineParentInfo>();
        var current = record;

        while (current.ParentId.HasValue)
        {
            var parent = allRecords.FirstOrDefault(r => r.Id == current.ParentId.Value);
            if (parent is null)
            {
                break;
            }

            parents.Add(new AzureDevOpsTimelineParentInfo
            {
                Name = parent.Name,
                Type = parent.RecordType,
                LogId = parent.Log?.Id ?? 0
            });

            current = parent;
        }

        return parents;
    }

    private async Task<VssConnection> CreateConnectionAsync(string organizationUrl)
    {
        var token = await _tokenProvider.GetTokenAsync(organizationUrl.TrimEnd('/') + "/");
        var credentials = new VssOAuthAccessTokenCredential(token);
        return new VssConnection(new Uri(organizationUrl), credentials);
    }

    internal static TimeSpan ParseAge(string age)
    {
        var span = TimeSpan.Zero;
        var match = Regex.Match(age, @"(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?");
        if (match.Groups[1].Success)
        {
            span += TimeSpan.FromDays(int.Parse(match.Groups[1].Value));
        }

        if (match.Groups[2].Success)
        {
            span += TimeSpan.FromHours(int.Parse(match.Groups[2].Value));
        }

        if (match.Groups[3].Success)
        {
            span += TimeSpan.FromMinutes(int.Parse(match.Groups[3].Value));
        }

        return span;
    }
}
