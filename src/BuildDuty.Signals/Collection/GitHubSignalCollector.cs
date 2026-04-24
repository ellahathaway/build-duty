using System.Text.RegularExpressions;
using BuildDuty.Signals.Configuration;
using Microsoft.Extensions.Logging;
using Octokit;

namespace BuildDuty.Signals.Collection;

/// <summary>
/// Collects signals from GitHub issues and pull requests.
/// </summary>
internal sealed class GitHubSignalCollector : ISignalCollector
{
    private readonly GitHubConfig _config;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger _logger;

    internal GitHubSignalCollector(GitHubConfig config, ITokenProvider tokenProvider, ILogger logger)
    {
        _config = config;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Signal>> CollectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Collecting GitHub signals");

        var signals = new List<Signal>();

        var repoTasks = _config.Organizations.SelectMany(org =>
            org.Repositories.Select(repo => CollectRepositoryAsync(org.Name, repo)));

        foreach (var repoSignals in await Task.WhenAll(repoTasks))
        {
            signals.AddRange(repoSignals);
        }

        _logger.LogInformation("Collected {Count} GitHub signals", signals.Count);
        return signals;
    }

    private async Task<List<Signal>> CollectRepositoryAsync(string organization, GitHubRepositoryConfig repo)
    {
        var signals = new List<Signal>();

        try
        {
            var client = await CreateClientAsync(organization, repo.Name);

            if (repo.Issues is { Count: > 0 })
            {
                var issueSignals = await CollectIssuesAsync(client, organization, repo.Name, repo.Issues);
                signals.AddRange(issueSignals);
            }

            if (repo.PullRequests is { Count: > 0 })
            {
                var prSignals = await CollectPullRequestsAsync(client, organization, repo.Name, repo.PullRequests);
                signals.AddRange(prSignals);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting {Organization}/{Repository}", organization, repo.Name);
        }

        return signals;
    }

    private async Task<List<GitHubIssueSignal>> CollectIssuesAsync(
        IGitHubClient client,
        string organization,
        string repoName,
        List<GitHubItemConfig> itemConfigs)
    {
        var signals = new List<GitHubIssueSignal>();

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open,
            Filter = IssueFilter.All,
        };

        var issues = await client.Issue.GetAllForRepository(organization, repoName, request);

        foreach (var issue in issues)
        {
            // Skip pull requests returned by the issues endpoint
            if (issue.PullRequest is not null)
            {
                continue;
            }

            var matchingConfig = FindMatchingConfig(issue, itemConfigs);
            if (matchingConfig is null)
            {
                continue;
            }

            signals.Add(new GitHubIssueSignal
            {
                Organization = organization,
                Repository = repoName,
                Url = issue.HtmlUrl.ToString(),
                Context = matchingConfig.Context,
                Item = BuildItemInfo(issue),
            });
        }

        return signals;
    }

    private async Task<List<GitHubPullRequestSignal>> CollectPullRequestsAsync(
        IGitHubClient client,
        string organization,
        string repoName,
        List<GitHubItemConfig> itemConfigs)
    {
        var signals = new List<GitHubPullRequestSignal>();

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open,
            Filter = IssueFilter.All,
        };

        var issues = await client.Issue.GetAllForRepository(organization, repoName, request);

        foreach (var issue in issues.Where(i => i.PullRequest is not null))
        {
            var matchingConfig = FindMatchingConfig(issue, itemConfigs);
            if (matchingConfig is null)
            {
                continue;
            }

            signals.Add(new GitHubPullRequestSignal
            {
                Organization = organization,
                Repository = repoName,
                Url = issue.HtmlUrl.ToString(),
                Context = matchingConfig.Context,
                Merged = issue.PullRequest?.Merged ?? false,
                Item = BuildItemInfo(issue),
            });
        }

        return signals;
    }

    private static GitHubItemConfig? FindMatchingConfig(Issue issue, List<GitHubItemConfig> configs)
    {
        foreach (var config in configs)
        {
            // Check name pattern
            if (!Regex.IsMatch(issue.Title, config.Name, RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Check author filter
            if (config.Authors.Count > 0 &&
                !config.Authors.Any(a => string.Equals(a, issue.User?.Login, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Check required labels
            if (config.Labels.Count > 0)
            {
                var issueLabels = issue.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!config.Labels.All(l => issueLabels.Contains(l)))
                {
                    continue;
                }
            }

            // Check excluded labels
            if (config.ExcludeLabels.Count > 0)
            {
                var issueLabels = issue.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (config.ExcludeLabels.Any(l => issueLabels.Contains(l)))
                {
                    continue;
                }
            }

            return config;
        }

        return null;
    }

    private static GitHubItemInfo BuildItemInfo(Issue issue)
    {
        return new GitHubItemInfo
        {
            Number = issue.Number,
            Title = issue.Title,
            State = issue.State.Value.ToString(),
            UpdatedAt = issue.UpdatedAt?.ToString("o"),
            Body = issue.Body,
        };
    }

    private async Task<IGitHubClient> CreateClientAsync(string organization, string repository)
    {
        var repoUrl = $"https://github.com/{organization}/{repository}";
        var token = await _tokenProvider.GetTokenAsync(repoUrl);
        return new GitHubClient(new ProductHeaderValue("BuildDutySignals"))
        {
            Credentials = new Credentials(token)
        };
    }
}
