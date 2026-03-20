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

    public async Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default)
    {
        var items = new List<WorkItem>();

        foreach (var org in _config.Organizations)
        {
            var client = await _clientFactory.CreateAsync(org.Url, ct);

            foreach (var project in org.Projects)
            {
                foreach (var pipeline in project.Pipelines)
                {
                    var statusFilter = pipeline.EffectiveStatus
                        .Select(s => s.ToLowerInvariant())
                        .ToHashSet();

                    var builds = await client.GetLatestBuildsAsync(project.Name, pipeline, ct);
                    foreach (var build in builds)
                    {
                        var resultName = build.Result?.ToString().ToLowerInvariant();
                        if (resultName is null || !statusFilter.Contains(resultName))
                            continue;

                        items.Add(ToWorkItem(org.Url, project.Name, pipeline, build));
                    }
                }
            }
        }

        return items;
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
            CorrelationId = $"corr_ado_{pipeline.Id}_{Sanitize(shortBranch)}",
            Signals =
            [
                new SignalReference { Type = "ado-pipeline-run", Ref = webUrl }
            ]
        };
    }

    private static string Sanitize(string value) =>
        value.Replace('/', '_').Replace('\\', '_');
}
