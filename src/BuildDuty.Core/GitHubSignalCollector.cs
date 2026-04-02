using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Maestro.Common;
using Octokit;

namespace BuildDuty.Core;

public class GitHubSignalCollector : SignalCollector<GitHubConfig>
{
    protected record RepositoryContext(string Organization, string RepositoryName, IGitHubClient Client);

    public GitHubSignalCollector(
        GitHubConfig config,
        IRemoteTokenProvider tokenProvider,
        IStorageProvider storageProvider)
        : base(config, tokenProvider, storageProvider)
    {
    }

    protected override async Task<List<Signal>> CollectCoreAsync()
    {
        var signals = await StorageProvider.GetSignalsFromWorkItemsAsync();

        var issueSignals = signals
            .Where(s => s.Type == SignalType.GitHubIssue)
            .OfType<GitHubIssueSignal>();

        var prSignals = signals
            .Where(s => s.Type == SignalType.GitHubPullRequest)
            .OfType<GitHubPullRequestSignal>();

        var collectedSignals = new List<Signal>();

        foreach (var org in Config.Organizations)
        {
            foreach (var repo in org.Repositories)
            {
                var client = await TokenProvider.GetGitHubClientAsync(org.Organization, repo.Name);
                var context = new RepositoryContext(org.Organization, repo.Name, client);

                var issueTask = CollectIssueSignalsAsync(context, repo.Issues, issueSignals);
                var prTask = CollectPullRequestSignalsAsync(context, repo.PullRequests, prSignals);

                await Task.WhenAll(issueTask, prTask);

                var repoSignals = issueTask.Result
                    .Cast<Signal>()
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

            var existingSignal = existingSignals.FirstOrDefault(s => s.TypedInfo.HtmlUrl == issue.HtmlUrl);
            if (existingSignal != null && existingSignal.TypedInfo.State.Value == issue.State.Value && existingSignal.TypedInfo.UpdatedAt == issue.UpdatedAt)
            {
                continue;
            }

            var signal = new GitHubIssueSignal(issue);
            if (existingSignal is null)
            {
                signals.Add(signal);
                continue;
            }

            signal.Id = existingSignal.Id; // Preserve the same ID for updates
            signal.WorkItemIds = existingSignal.WorkItemIds; // Preserve linked work items
            signals.Add(signal);
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
                var signal = new GitHubPullRequestSignal(pr);
                var existingSignal = existingSignals.FirstOrDefault(s => s.TypedInfo.HtmlUrl == pr.HtmlUrl);

                if (existingSignal is not null)
                {
                    if (existingSignal.TypedInfo.State.Value == pr.State.Value
                        && existingSignal.TypedInfo.UpdatedAt == pr.UpdatedAt)
                    {
                        continue;
                    }
                    signal.Id = existingSignal.Id; // Preserve the same ID for updates
                    signal.WorkItemIds = existingSignal.WorkItemIds; // Preserve linked work items
                    signals.Add(signal);
                    continue;
                }

                signals.Add(signal);
            }
        }

        return signals
            .GroupBy(signal => signal.TypedInfo.HtmlUrl, StringComparer.OrdinalIgnoreCase)
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
}
