using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Extension methods on <see cref="BuildHttpClient"/> for common query patterns.
/// </summary>
public static class BuildHttpClientExtensions
{
    /// <summary>
    /// Returns the single latest completed build per branch that matches the
    /// status filter. When no branches are configured, returns the single
    /// latest build across all branches.
    /// </summary>
    public static async Task<IReadOnlyList<Build>> GetLatestBuildsAsync(
        this BuildHttpClient client,
        string project,
        AzureDevOpsPipelineConfig pipeline,
        CancellationToken ct = default)
    {
        var resultFilter = ParseResultFilter(pipeline.EffectiveStatus);
        var builds = new List<Build>();

        if (pipeline.Branches is { Count: > 0 })
        {
            foreach (var branch in pipeline.Branches)
            {
                var build = await client.FetchLatestBuildAsync(project, pipeline.Id, resultFilter, branch, ct);
                if (build is not null)
                    builds.Add(build);
            }
        }
        else
        {
            var build = await client.FetchLatestBuildAsync(project, pipeline.Id, resultFilter, branch: null, ct);
            if (build is not null)
                builds.Add(build);
        }

        return builds;
    }

    /// <summary>
    /// Fetches the single most recent completed build matching the given filters.
    /// </summary>
    public static async Task<Build?> FetchLatestBuildAsync(
        this BuildHttpClient client,
        string project,
        int definitionId,
        BuildResult resultFilter,
        string? branch,
        CancellationToken ct = default)
    {
        var branchName = branch is not null ? $"refs/heads/{branch}" : null;

        var results = await client.GetBuildsAsync(
            project: project,
            definitions: [definitionId],
            branchName: branchName,
            statusFilter: BuildStatus.Completed,
            resultFilter: resultFilter,
            top: 1,
            cancellationToken: ct);

        return results.FirstOrDefault();
    }

    private static BuildResult ParseResultFilter(IReadOnlyList<string> statuses)
    {
        var result = BuildResult.None;
        foreach (var s in statuses)
        {
            result |= s.ToLowerInvariant() switch
            {
                "failed" => BuildResult.Failed,
                "partiallysucceeded" => BuildResult.PartiallySucceeded,
                "canceled" => BuildResult.Canceled,
                "succeeded" => BuildResult.Succeeded,
                _ => throw new ArgumentException($"Unknown pipeline status filter: '{s}'")
            };
        }
        return result;
    }
}
