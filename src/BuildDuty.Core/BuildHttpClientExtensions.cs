using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Extension methods on <see cref="BuildHttpClient"/> for common query patterns.
/// </summary>
public static class BuildHttpClientExtensions
{
    /// <summary>
    /// Returns the single latest completed build per branch along with the
    /// resolved branch list. When <see cref="AzureDevOpsPipelineConfig.Release"/>
    /// is set, resolves release branches dynamically before fetching.
    /// </summary>
    public static async Task<(IReadOnlyList<Build> Builds, IReadOnlyList<string> ResolvedBranches)> GetLatestBuildsAsync(
        this BuildHttpClient client,
        string project,
        AzureDevOpsPipelineConfig pipeline,
        GitHttpClient? gitClient = null,
        CancellationToken ct = default)
    {
        var branches = pipeline.Branches;

        // If release config is set, resolve release branches dynamically
        if (pipeline.Release is not null && gitClient is not null)
        {
            var resolver = new ReleaseBranchResolver();
            var resolved = await resolver.ResolveAsync(gitClient, project, pipeline.Release, ct);
            branches = resolved.ToList();
        }

        var builds = new List<Build>();

        if (branches is { Count: > 0 })
        {
            foreach (var branch in branches)
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

        return (builds, branches);
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
