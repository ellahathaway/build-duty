using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Maestro.Common;
using Octokit;

namespace BuildDuty.Core;

public class GitHubSignalCollector(IBuildDutyConfigProvider configProvider, IRemoteTokenProvider tokenProvider, IStorageProvider storageProvider)
    : SignalCollector<GitHubConfig>(configProvider, tokenProvider, storageProvider)
{
    protected override GitHubConfig ResolveConfig(BuildDutyConfig config) =>
        config.GitHub ?? throw new InvalidOperationException("GitHub config not found.");
    protected override async Task<List<ISignal>> CollectCoreAsync()
    {
        var signals = await StorageProvider.GetSignalsFromWorkItemsAsync();

        var issueSignals = signals
            .Where(s => s.Type == SignalType.GitHubIssue)
            .OfType<GitHubIssueSignal>();

        var prSignals = signals
            .Where(s => s.Type == SignalType.GitHubPullRequest)
            .OfType<GitHubPullRequestSignal>();

        var collectedSignals = new List<ISignal>();

        foreach (var org in Config.Organizations)
        {
            foreach (var repo in org.Repositories)
            {
                var context = await CreateRepositoryContextAsync(org.Organization, repo.Name);

                var issueTask = CollectIssueSignalsAsync(context, repo.Issues, issueSignals);
                var prTask = CollectPullRequestSignalsAsync(context, repo.PullRequests, prSignals);

                await Task.WhenAll(issueTask, prTask);

                var repoSignals = issueTask.Result
                    .Cast<ISignal>()
                    .Concat(prTask.Result)
                    .ToList();

                collectedSignals.AddRange(repoSignals);
            }
        }

        return collectedSignals;
    }

    private static async Task<List<GitHubIssueSignal>> CollectIssueSignalsAsync(
        RepositoryContext context,
        GitHubIssueConfig? issueConfig,
        IEnumerable<GitHubIssueSignal> existingSignals)
    {
        if (issueConfig is null)
        {
            return [];
        }

        var request = new RepositoryIssueRequest
        {
            State = issueConfig.State
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

            var existingSignal = existingSignals.FirstOrDefault(s => s.Info.HtmlUrl == issue.HtmlUrl);

            if (existingSignal is null)
            {
                signals.Add(new GitHubIssueSignal(issue));
                continue;
            }

            if (existingSignal.Info.State.Value == issue.State.Value && existingSignal.Info.UpdatedAt == issue.UpdatedAt)
            {
                continue;
            }

            existingSignal.Info = issue;
            signals.Add(existingSignal);
        }

        return signals;
    }

    private static async Task<List<GitHubPullRequestSignal>> CollectPullRequestSignalsAsync(
        RepositoryContext context,
        List<GitHubPullRequestPattern>? prPatterns,
        IEnumerable<GitHubPullRequestSignal> existingSignals
        )
    {
        if (prPatterns is null || prPatterns.Count == 0)
        {
            return [];
        }

        var patternsByState = prPatterns
            .Where(pattern => pattern.Name is not null)
            .GroupBy(pattern => pattern.State)
            .ToList();

        var signals = new List<GitHubPullRequestSignal>();

        foreach (var patternGroup in patternsByState)
        {
            var pulls = await context.Client.PullRequest.GetAllForRepository(
                context.Organization, context.RepositoryName,
                new PullRequestRequest { State = patternGroup.Key });

            var matchingPulls = pulls.Where(pr => MatchesAnyPattern(pr.Title, patternGroup.Select(p => p.Name)));

            foreach (var pr in matchingPulls)
            {
                var existingSignal = existingSignals.FirstOrDefault(s => s.Info.HtmlUrl == pr.HtmlUrl);

                if (existingSignal is not null)
                {
                    if (existingSignal.Info.State.Value == pr.State.Value
                        && existingSignal.Info.UpdatedAt == pr.UpdatedAt)
                    {
                        continue;
                    }
                    existingSignal.Info = pr;
                    signals.Add(existingSignal);
                    continue;
                }

                signals.Add(new GitHubPullRequestSignal(pr));
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
