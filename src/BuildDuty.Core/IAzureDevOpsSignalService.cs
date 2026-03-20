using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Collects pipeline run signals from Azure DevOps.
/// </summary>
public interface IAzureDevOpsSignalService : ISignalService;

/// <summary>
/// Collects pipeline run signals from Azure DevOps using the official client SDK.
/// For each configured pipeline, fetches the single latest completed build per
/// branch (or across all branches when none are specified) that matches the
/// configured status filter.
/// </summary>
public sealed class AzureDevOpsSignalService : IAzureDevOpsSignalService
{
    private readonly AzureDevOpsConfig _config;
    private readonly IBuildHttpClientFactory _clientFactory;

    public AzureDevOpsSignalService(AzureDevOpsConfig config, IBuildHttpClientFactory clientFactory)
    {
        _config = config;
        _clientFactory = clientFactory;
    }

    public string SourceName => "AzureDevOps";

    /// <summary>
    /// The set of active correlation ID prefixes from the last scan.
    /// For pipelines with release branch resolution, this contains the
    /// correlation IDs for the currently-resolved branches. Work items
    /// whose correlation ID matches a pipeline prefix but is NOT in this
    /// set are stale and should be auto-resolved.
    /// </summary>
    public HashSet<string> ActiveCorrelationIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Correlation ID prefixes for pipelines that use release branch
    /// resolution. Any existing work item matching these prefixes but
    /// not in <see cref="ActiveCorrelationIds"/> is stale.
    /// </summary>
    public HashSet<string> ReleasePipelinePrefixes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Correlation IDs where the latest build now passes (doesn't match
    /// the failure filter). Existing work items with these IDs can be resolved.
    /// </summary>
    public HashSet<string> PassingCorrelationIds { get; } = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default)
    {
        var items = new List<WorkItem>();

        foreach (var org in _config.Organizations)
        {
            var client = await _clientFactory.CreateAsync(org.Url, ct);

            var needsGit = org.Projects.SelectMany(p => p.Pipelines).Any(p => p.Release is not null);
            var gitClient = needsGit
                ? await _clientFactory.CreateGitClientAsync(org.Url, ct)
                : null;

            foreach (var project in org.Projects)
            {
                foreach (var pipeline in project.Pipelines)
                {
                    var statusFilter = pipeline.EffectiveStatus
                        .Select(s => s.ToLowerInvariant())
                        .ToHashSet();

                    var (builds, resolvedBranches) = await client.GetLatestBuildsAsync(
                        project.Name, pipeline, gitClient, ct);

                    // Track active correlation IDs for release pipelines
                    if (pipeline.Release is not null && resolvedBranches.Count > 0)
                    {
                        ReleasePipelinePrefixes.Add($"corr_ado_{pipeline.Id}_");
                        foreach (var branch in resolvedBranches)
                            ActiveCorrelationIds.Add($"corr_ado_{pipeline.Id}_{Sanitize(branch)}");
                    }

                    foreach (var build in builds)
                    {
                        var resultName = build.Result?.ToString().ToLowerInvariant();
                        var corrId = CorrelationIdFor(pipeline.Id, build);

                        if (resultName is not null && statusFilter.Contains(resultName))
                        {
                            items.Add(ToWorkItem(org.Url, project.Name, pipeline, build));
                        }
                        else
                        {
                            // Latest build is passing — track so we can resolve old items
                            PassingCorrelationIds.Add(corrId);
                        }
                    }
                }
            }
        }

        return items;
    }

    private static string CorrelationIdFor(int pipelineId, Build build)
    {
        var shortBranch = build.SourceBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? build.SourceBranch["refs/heads/".Length..]
            : build.SourceBranch;
        return $"corr_ado_{pipelineId}_{Sanitize(shortBranch)}";
    }

    private static WorkItem ToWorkItem(string orgUrl, string project, AzureDevOpsPipelineConfig pipeline, Build build)
    {
        var shortBranch = build.SourceBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? build.SourceBranch["refs/heads/".Length..]
            : build.SourceBranch;

        var webUrl = build.Links?.Links?.TryGetValue("web", out var webLink) == true
                     && webLink is ReferenceLink refLink
            ? refLink.Href
            : $"{orgUrl.TrimEnd('/')}/{project}/_build/results?buildId={build.Id}";

        return new WorkItem
        {
            Id = $"wi_ado_{build.Id}",
            State = WorkItemState.Unresolved,
            Title = $"[{pipeline.Name}] {shortBranch} — Build #{build.BuildNumber} {build.Result?.ToString().ToLowerInvariant()}",
            CorrelationId = CorrelationIdFor(pipeline.Id, build),
            Signals =
            [
                new SignalReference { Type = "ado-pipeline-run", Ref = webUrl }
            ]
        };
    }

    private static string Sanitize(string value) =>
        value.Replace('/', '_').Replace('\\', '_');
}
