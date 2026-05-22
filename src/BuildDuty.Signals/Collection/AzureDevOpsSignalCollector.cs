using System.Text.RegularExpressions;
using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using Maestro.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Signals.Collection;

/// <summary>
/// Collects pipeline signals from Azure DevOps.
/// </summary>
internal sealed class AzureDevOpsSignalCollector : ISignalCollector
{
    private readonly AzureDevOpsConfig _config;
    private readonly IRemoteTokenProvider _tokenProvider;
    private readonly ReleaseBranchResolver _branchResolver;
    private readonly ILogger _logger;

    internal AzureDevOpsSignalCollector(AzureDevOpsConfig config, IRemoteTokenProvider tokenProvider, ILogger logger, ReleaseBranchResolver branchResolver)
    {
        _config = config;
        _tokenProvider = tokenProvider;
        _logger = logger;
        _branchResolver = branchResolver;
    }

    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collecting Azure DevOps signals");

        var signals = new List<Signal>();
        var scopes = new List<CollectedScope>();
        var failures = new List<CollectionFailure>();

        var orgTasks = _config.Organizations.Select(async organization =>
        {
            _logger.LogDebug("  Organization: {Org}", organization.Url);
            var projectTasks = organization.Projects
                .Select(project => CollectProjectSignalsAsync(
                    organization.Url,
                    project,
                    cancellationToken));

            var projectResults = await Task.WhenAll(projectTasks);
            return projectResults;
        });

        foreach (var projectResults in await Task.WhenAll(orgTasks))
        {
            foreach (var result in projectResults)
            {
                signals.AddRange(result.Signals);
                scopes.AddRange(result.Scopes);
                failures.AddRange(result.Failures);
            }
        }

        _logger.LogInformation("Collected {Count} Azure DevOps signals", signals.Count);
        return new CollectionResult(signals, scopes, failures);
    }

    private async Task<(List<Signal> Signals, List<CollectedScope> Scopes, List<CollectionFailure> Failures)> CollectProjectSignalsAsync(
        string orgUrl,
        AzureDevOpsProjectConfig project,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("  Project: {Org}/{Project} ({PipelineCount} pipelines)", orgUrl, project.Name, project.Pipelines.Count);

        var signals = new List<Signal>();
        var scopes = new List<CollectedScope>();
        var failures = new List<CollectionFailure>();

        var pipelineTasks = project.Pipelines
            .Select(pipeline => CollectPipelineSignalsAsync(orgUrl, project.Name, pipeline, cancellationToken));
        var pipelineResults = await Task.WhenAll(pipelineTasks);

        foreach (var result in pipelineResults)
        {
            signals.AddRange(result.Signals);
            scopes.AddRange(result.Scopes);
            failures.AddRange(result.Failures);
        }

        return (signals, scopes, failures);
    }

    private async Task<(List<AzureDevOpsPipelineSignal> Signals, List<CollectedScope> Scopes, List<CollectionFailure> Failures)> CollectPipelineSignalsAsync(
        string orgUrl,
        string projectName,
        AzureDevOpsPipelineConfig pipeline,
        CancellationToken cancellationToken)
    {
        var signals = new List<AzureDevOpsPipelineSignal>();
        var scopes = new List<CollectedScope>();
        var failures = new List<CollectionFailure>();
        var branches = pipeline.Branches.ToList();

        // If release branch config is set, resolve branches dynamically
        if (branches.Count == 0 && pipeline.Release is not null)
        {
            try
            {
                branches = await _branchResolver.ResolveAsync(orgUrl, projectName, pipeline.Id, pipeline.Release);
                _logger.LogInformation("  '{PipelineName}' (id:{PipelineId}): {Count} branches resolved", pipeline.Name, pipeline.Id, branches.Count);
                if (branches.Count > 0)
                {
                    _logger.LogDebug("    '{PipelineName}' branches: {Branches}", pipeline.Name, string.Join(", ", branches));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving release branches for pipeline {PipelineName}", pipeline.Name);
                failures.Add(new CollectionFailure($"azdo:{orgUrl}/{projectName}/{pipeline.Id}/release-branches", ex.Message));
                return (signals, scopes, failures);
            }
        }
        else if (branches.Count > 0)
        {
            _logger.LogInformation("  '{PipelineName}' (id:{PipelineId}): {Count} configured branches", pipeline.Name, pipeline.Id, branches.Count);
            _logger.LogDebug("    '{PipelineName}' branches: {Branches}", pipeline.Name, string.Join(", ", branches));
        }

        if (branches.Count == 0)
        {
            _logger.LogDebug("  '{PipelineName}' (id:{PipelineId}): no branches to check, skipping", pipeline.Name, pipeline.Id);
            return (signals, scopes, failures);
        }

        // Collect signals for all branches in parallel using a shared client
        using var buildClient = await _tokenProvider.GetAzureDevOpsBuildClient(orgUrl);
        var branchTasks = branches.Select(branch => CollectBranchSignalAsync(
            orgUrl, projectName, pipeline, branch, buildClient, cancellationToken));
        var branchResults = await Task.WhenAll(branchTasks);

        foreach (var result in branchResults)
        {
            if (result.Signal is not null)
            {
                signals.Add(result.Signal);
                scopes.Add(result.Scope!);
            }
            if (result.Failure is not null)
            {
                failures.Add(result.Failure);
            }
        }

        return (signals, scopes, failures);
    }

    private async Task<(AzureDevOpsPipelineSignal? Signal, CollectedScope? Scope, CollectionFailure? Failure)> CollectBranchSignalAsync(
        string orgUrl,
        string projectName,
        AzureDevOpsPipelineConfig pipeline,
        string branch,
        BuildHttpClient buildClient,
        CancellationToken cancellationToken)
    {
        var scopeKey = $"azdo:{orgUrl}/{projectName}/{pipeline.Id}/{branch}";
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
                _logger.LogDebug("    {Pipeline}/{Branch}: no builds found", pipeline.Name, branch);
                return (null, null, null);
            }

            // Check age filter
            if (!string.IsNullOrEmpty(pipeline.Age) && build.FinishTime.HasValue)
            {
                var maxAge = ParseAge(pipeline.Age);
                if (DateTime.UtcNow - build.FinishTime.Value > maxAge)
                {
                    _logger.LogDebug("    {Pipeline}/{Branch}: build {BuildId} too old (finished {FinishTime})", pipeline.Name, branch, build.Id, build.FinishTime);
                    return (null, null, null);
                }
            }

            // Only collect signals for configured failure statuses
            if (build.Result is null || !pipeline.Status.Contains(build.Result.Value))
            {
                _logger.LogDebug("    {Pipeline}/{Branch}: build {BuildId} result={Result}, not monitored", pipeline.Name, branch, build.Id, build.Result);
                return (null, null, null);
            }

            _logger.LogDebug("    {Pipeline}/{Branch}: build {BuildId} result={Result} — collecting signal", pipeline.Name, branch, build.Id, build.Result);
            var timelineRecords = await CollectTimelineRecordsAsync(buildClient, projectName, build.Id, pipeline, cancellationToken);

            // Skip if timeline filters are configured but no records matched
            if (pipeline.TimelineFilters is { Count: > 0 } && timelineRecords.Count == 0)
            {
                _logger.LogDebug("    {Pipeline}/{Branch}: no timeline records matched filters, skipping", pipeline.Name, branch);
                return (null, null, null);
            }

            var signal = new AzureDevOpsPipelineSignal
            {
                OrganizationUrl = orgUrl,
                ProjectName = projectName,
                PipelineId = pipeline.Id,
                Url = $"{orgUrl}/{projectName}/_build/results?buildId={build.Id}",
                Context = pipeline.Context?.Trim(),
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

            return (signal, new CollectedScope(scopeKey), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting pipeline {PipelineName} branch {Branch}", pipeline.Name, branch);
            return (null, null, new CollectionFailure(scopeKey, ex.Message));
        }
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

                records.Add(new AzureDevOpsTimelineRecordInfo
                {
                    Result = record.Result.Value,
                    RecordType = record.RecordType,
                    Name = record.Name,
                    LogId = record.Log?.Id ?? 0
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
            var recordType = Enum.TryParse<TimelineRecordType>(record.RecordType, true, out var rt) ? rt : (TimelineRecordType?)null;
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
