using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Extension methods on <see cref="BuildHttpClient"/> for common query patterns.
/// </summary>
public static class BuildHttpClientExtensions
{
    /// <summary>
    /// Returns the single latest completed build per branch. When no branches
    /// are configured, returns the single latest build across all branches.
    /// No result filter is applied — the caller decides which results to act on.
    /// </summary>
    public static async Task<IReadOnlyList<Build>> GetLatestBuildsAsync(
        this BuildHttpClient client,
        string project,
        AzureDevOpsPipelineConfig pipeline,
        CancellationToken ct = default)
    {
        var builds = new List<Build>();

        if (pipeline.Branches is { Count: > 0 })
        {
            foreach (var branch in pipeline.Branches)
            {
                var build = await client.FetchLatestBuildAsync(project, pipeline.Id, branch, ct);
                if (build is not null)
                    builds.Add(build);
            }
        }
        else
        {
            var build = await client.FetchLatestBuildAsync(project, pipeline.Id, branch: null, ct);
            if (build is not null)
                builds.Add(build);
        }

        return builds;
    }

    /// <summary>
    /// Fetches the single most recent completed build for a pipeline, optionally
    /// filtered to a specific branch.
    /// </summary>
    public static async Task<Build?> FetchLatestBuildAsync(
        this BuildHttpClient client,
        string project,
        int definitionId,
        string? branch,
        CancellationToken ct = default)
    {
        var branchName = branch is not null ? $"refs/heads/{branch}" : null;

        var results = await client.GetBuildsAsync(
            project: project,
            definitions: [definitionId],
            branchName: branchName,
            statusFilter: BuildStatus.Completed,
            top: 1,
            cancellationToken: ct);

        return results.FirstOrDefault();
    }
}
