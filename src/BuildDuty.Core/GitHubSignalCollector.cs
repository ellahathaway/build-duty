using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Maestro.Common;
using Octokit;

namespace BuildDuty.Core;

public class GitHubSignalCollector(GitHubConfig config, IRemoteTokenProvider tokenProvider, IWorkItemsProvider workItemsProvider)
    : SignalCollector<GitHubConfig>(config, tokenProvider, workItemsProvider)
{
    protected override async Task<List<ISignal>> CollectCoreAsync(CancellationToken ct = default)
    {
        var existingIssuesByUrl = (await WorkItemsProvider.GetWorkItemsAsync(GitHubSignalType.Issue, ct))
            .SelectMany(item => item.Signals)
            .OfType<GitHubIssueSignal>()
            .ToDictionary(s => s.Info.HtmlUrl, StringComparer.OrdinalIgnoreCase);

        var existingPrsByUrl = (await WorkItemsProvider.GetWorkItemsAsync(GitHubSignalType.PullRequest, ct))
            .SelectMany(item => item.Signals)
            .OfType<GitHubPullRequestSignal>()
            .ToDictionary(s => s.Info.HtmlUrl, StringComparer.OrdinalIgnoreCase);

        var signals = new List<ISignal>();

        foreach (var org in Config.Organizations)
        {
            foreach (var repo in org.Repositories)
            {
                ct.ThrowIfCancellationRequested();

                var context = await CreateRepositoryContextAsync(org.Organization, repo.Name);

                var issueTask = CollectIssueSignalsAsync(context, repo.Issues, existingIssuesByUrl, ct);
                var prTask = CollectPullRequestSignalsAsync(context, repo.PullRequests, existingPrsByUrl, ct);

                await Task.WhenAll(issueTask, prTask);

                signals.AddRange(issueTask.Result);
                signals.AddRange(prTask.Result);
            }
        }

        return signals;
    }

    private static async Task<List<GitHubIssueSignal>> CollectIssueSignalsAsync(
        RepositoryContext context,
        GitHubIssueConfig? issueConfig,
        Dictionary<string, GitHubIssueSignal> existingByUrl,
        CancellationToken ct)
    {
        if (issueConfig is null)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();

        var request = new RepositoryIssueRequest
        {
            State = issueConfig.State,
        };

        foreach (var label in issueConfig.Labels)
        {
            request.Labels.Add(label);
        }

        var issues = await context.Client.Issue.GetAllForRepository(
            context.Organization, context.RepositoryName, request);

        var signals = new List<GitHubIssueSignal>();

        foreach (var issue in issues)
        {
            if (issue.PullRequest is not null)
            {
                continue; // GitHub API returns PRs as issues too
            }

            existingByUrl.TryGetValue(issue.HtmlUrl, out var existing);

            if (existing is not null
                && existing.Info.State.Value == issue.State.Value
                && existing.Info.UpdatedAt == issue.UpdatedAt)
            {
                continue;
            }

            signals.Add(GitHubIssueSignal.Create(issue, existing?.WorkItemIds));
        }

        return signals;
    }

    private static async Task<List<GitHubPullRequestSignal>> CollectPullRequestSignalsAsync(
        RepositoryContext context,
        List<GitHubPullRequestPattern>? prPatterns,
        Dictionary<string, GitHubPullRequestSignal> existingByUrl,
        CancellationToken ct)
    {
        if (prPatterns is null || prPatterns.Count == 0)
        {
            return [];
        }

        ct.ThrowIfCancellationRequested();

        var patternsByState = prPatterns
            .Where(pattern => pattern.Name is not null)
            .GroupBy(pattern => pattern.State)
            .ToList();

        var signals = new List<GitHubPullRequestSignal>();

        foreach (var patternGroup in patternsByState)
        {
            ct.ThrowIfCancellationRequested();

            var pulls = await context.Client.PullRequest.GetAllForRepository(
                context.Organization, context.RepositoryName,
                new PullRequestRequest { State = patternGroup.Key });

            var matchingPulls = pulls.Where(pr => MatchesAnyPattern(pr.Title, patternGroup.Select(p => p.Name)));

            foreach (var pr in matchingPulls)
            {
                existingByUrl.TryGetValue(pr.HtmlUrl, out var existing);

                if (existing is not null
                    && existing.Info.State.Value == pr.State.Value
                    && existing.Info.UpdatedAt == pr.UpdatedAt)
                {
                    continue;
                }

                signals.Add(GitHubPullRequestSignal.Create(pr, existing?.WorkItemIds));
            }
        }

        return signals
            .GroupBy(signal => signal.Info.HtmlUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static bool MatchesAnyPattern(string title, IEnumerable<Regex> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(title))
            {
                return true;
            }
        }

        return false;
    }

    protected record RepositoryContext
    {
        public required string Organization { get; init; }
        public required string RepositoryName { get; init; }
        public required IGitHubClient Client { get; init; }
    }

    protected virtual async Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
    {
        var repoUrl = $"https://github.com/{organization}/{repository}";
        var token = await TokenProvider.GetTokenForRepositoryAsync(repoUrl);

        var client = new GitHubClient(new ProductHeaderValue("build-duty"))
        {
            Credentials = new Credentials(token),
        };

        return new RepositoryContext
        {
            Organization = organization,
            RepositoryName = repository,
            Client = client,
        };
    }
}
