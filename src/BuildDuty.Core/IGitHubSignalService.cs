using BuildDuty.Core.Models;
using Octokit;

namespace BuildDuty.Core;

/// <summary>
/// Collects issue and pull request signals from GitHub.
/// </summary>
public interface IGitHubSignalService : ISignalService;

/// <summary>
/// Collects issue and pull request signals from GitHub using Octokit.
/// For each configured repository, fetches issues and/or PRs matching
/// the configured labels and state filters.
/// </summary>
public sealed class GitHubSignalService : IGitHubSignalService
{
    private readonly GitHubConfig _config;
    private readonly IGitHubClient _client;

    public GitHubSignalService(GitHubConfig config, IGitHubClient client)
    {
        _config = config;
        _client = client;
    }

    public string SourceName => "GitHub";

    public async Task<IReadOnlyList<WorkItem>> CollectAsync(CancellationToken ct = default)
    {
        var items = new List<WorkItem>();

        foreach (var repo in _config.Repositories)
        {
            if (repo.Issues is not null)
            {
                var issues = await FetchIssuesAsync(repo, repo.Issues);
                items.AddRange(issues.Select(i => ToWorkItem(repo, i)));
            }

            if (repo.PullRequests is not null)
            {
                var prs = await FetchPullRequestsAsync(repo, repo.PullRequests);
                items.AddRange(prs.Select(pr => PrToWorkItem(repo, pr)));
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<Issue>> FetchIssuesAsync(
        GitHubRepositoryConfig repo,
        GitHubIssueConfig issueConfig)
    {
        var request = new RepositoryIssueRequest
        {
            State = ParseItemStateFilter(issueConfig.EffectiveState),
            Filter = IssueFilter.All,
        };

        foreach (var label in issueConfig.Labels)
            request.Labels.Add(label);

        var issues = await _client.Issue.GetAllForRepository(
            repo.Owner, repo.Name, request);

        // Octokit returns PRs as issues too — filter them out
        return issues.Where(i => i.PullRequest is null).ToList();
    }

    private async Task<IReadOnlyList<PullRequest>> FetchPullRequestsAsync(
        GitHubRepositoryConfig repo,
        GitHubPullRequestConfig prConfig)
    {
        var request = new PullRequestRequest
        {
            State = ParseItemStateFilter(prConfig.EffectiveState),
        };

        var prs = await _client.PullRequest.GetAllForRepository(
            repo.Owner, repo.Name, request);

        if (prConfig.Labels is { Count: > 0 })
        {
            var labelSet = new HashSet<string>(prConfig.Labels, StringComparer.OrdinalIgnoreCase);
            prs = prs.Where(pr =>
                pr.Labels.Any(l => labelSet.Contains(l.Name))).ToList();
        }

        return prs.ToList();
    }

    private static ItemStateFilter ParseItemStateFilter(string state) =>
        state.ToLowerInvariant() switch
        {
            "open" => ItemStateFilter.Open,
            "closed" => ItemStateFilter.Closed,
            "all" => ItemStateFilter.All,
            _ => throw new ArgumentException($"Unknown GitHub state filter: '{state}'")
        };

    private static WorkItem ToWorkItem(GitHubRepositoryConfig repo, Issue issue)
    {
        return new WorkItem
        {
            Id = $"wi_gh_issue_{repo.Owner}_{repo.Name}_{issue.Number}",
            State = WorkItemState.Unresolved,
            Title = $"[{repo.Owner}/{repo.Name}#{issue.Number}] {issue.Title}",
            CorrelationId = $"corr_gh_{repo.Owner}_{repo.Name}_issue_{issue.Number}",
            Signals =
            [
                new SignalReference { Type = "github-issue", Ref = issue.HtmlUrl }
            ]
        };
    }

    private static WorkItem PrToWorkItem(GitHubRepositoryConfig repo, PullRequest pr)
    {
        return new WorkItem
        {
            Id = $"wi_gh_pr_{repo.Owner}_{repo.Name}_{pr.Number}",
            State = WorkItemState.Unresolved,
            Title = $"[{repo.Owner}/{repo.Name}#{pr.Number}] {pr.Title}",
            CorrelationId = $"corr_gh_{repo.Owner}_{repo.Name}_pr_{pr.Number}",
            Signals =
            [
                new SignalReference { Type = "github-pr", Ref = pr.HtmlUrl }
            ]
        };
    }
}

