using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Octokit;

namespace BuildDuty.Core;

public class GitHubSignalCollector : SignalCollector<GitHubConfig>
{
    protected record RepositoryContext(string Organization, string RepositoryName, IGitHubClient Client);

    public GitHubSignalCollector(
        GitHubConfig config,
        IGeneralTokenProvider tokenProvider,
        IStorageProvider storageProvider)
        : base(config, tokenProvider, storageProvider)
    {
    }

    protected override async Task<List<Signal>> CollectCoreAsync()
    {
        var existingSignals = (await StorageProvider.GetAllSignalsAsync())
            .Where(s => s.Type == SignalType.GitHubIssue || s.Type == SignalType.GitHubPullRequest)
            .ToList();

        // Group all configs and existing signals by org/repo so we collect
        // once per repo and only gather missed signals a single time.
        var repoGroups = Config.Organizations
            .SelectMany(org => org.Repositories.Select(repo => (Org: org.Organization, Repo: repo)))
            .GroupBy(x => (x.Org, x.Repo.Name))
            .Select(g =>
            {
                var org = g.Key.Org;
                var repoName = g.Key.Name;
                var itemConfigs = g.SelectMany(x => (x.Repo.Issues ?? []).Concat(x.Repo.PullRequests ?? [])).ToList();
                var repoPrefix = $"https://github.com/{org}/{repoName}/";
                var repoSignals = existingSignals
                    .Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return (Org: org, RepoName: repoName, ItemConfigs: itemConfigs, ExistingSignals: repoSignals);
            });

        var collectedSignals = new List<Signal>();

        var repoTasks = repoGroups.Select(async group =>
        {
            var (org, repoName, itemConfigs, repoSignals) = group;
            var context = await CreateRepositoryContextAsync(org, repoName);
            return await CollectItemSignalsAsync(context, itemConfigs, repoSignals);
        });

        var results = await Task.WhenAll(repoTasks);
        foreach (var signals in results)
        {
            collectedSignals.AddRange(signals);
        }

        return collectedSignals;
    }

    protected virtual async Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
    {
        var client = await TokenProvider.GetGitHubClientAsync(organization, repository);
        return new RepositoryContext(organization, repository, client);
    }

    private static async Task<List<Signal>> CollectItemSignalsAsync(
        RepositoryContext context,
        IEnumerable<GitHubItemConfig> itemConfigs,
        IEnumerable<Signal> existingSignals)
    {
        var signals = new List<Signal>();
        var seenUrls = new HashSet<Uri>();

        foreach (var itemConfig in itemConfigs)
        {
            var request = new RepositoryIssueRequest
            {
                State = itemConfig.State
            };

            foreach (var label in itemConfig.Labels)
            {
                request.Labels.Add(label);
            }

            var items = (await context.Client.Issue.GetAllForRepository(context.Organization, context.RepositoryName, request))
                .Where(i => MatchesItemConfig(i, itemConfig));

            foreach (var item in items)
            {
                var itemUrl = new Uri(item.HtmlUrl);
                seenUrls.Add(itemUrl);

                var existingSignal = existingSignals.FirstOrDefault(s => s.Url == itemUrl);
                if (existingSignal != null && GetUpdatedAt(existingSignal) == item.UpdatedAt)
                {
                    continue;
                }

                var comments = await GetItemCommentsAsync(context, item.Number);
                var issueInfo = new GitHubIssueInfo(item.Number, item.Title, item.State.Value.ToString(), item.UpdatedAt, item.Body, comments);

                Signal signal;
                if (item.PullRequest != null)
                {
                    var pr = await context.Client.PullRequest.Get(context.Organization, context.RepositoryName, item.Number);
                    var checks = await GetPullRequestChecksAsync(context, pr.Head.Sha);
                    signal = new GitHubPullRequestSignal(new GitHubPullRequestInfo(issueInfo, pr.Merged, checks), itemUrl);
                }
                else
                {
                    signal = new GitHubIssueSignal(issueInfo, itemUrl);
                }

                signal.Context = itemConfig.Context;
                if (existingSignal is not null)
                {
                    signal.PreserveFrom(existingSignal);
                }
                signals.Add(signal);
            }
        }

        // Re-fetch existing signals that weren't seen in the query results.
        foreach (var existing in existingSignals.Where(s => !seenUrls.Contains(s.Url)))
        {
            try
            {
                var item = await context.Client.Issue.Get(
                    context.Organization,
                    context.RepositoryName,
                    GetNumber(existing));

                if (GetUpdatedAt(existing) == item.UpdatedAt)
                {
                    continue;
                }

                var comments = await GetItemCommentsAsync(context, item.Number);
                var issueInfo = new GitHubIssueInfo(
                    item.Number, item.Title, item.State.Value.ToString(),
                    item.UpdatedAt, item.Body, comments);

                var itemUrl = new Uri(item.HtmlUrl);

                Signal signal;
                if (item.PullRequest != null)
                {
                    var pr = await context.Client.PullRequest.Get(context.Organization, context.RepositoryName, item.Number);
                    var checks = await GetPullRequestChecksAsync(context, pr.Head.Sha);
                    signal = new GitHubPullRequestSignal(new GitHubPullRequestInfo(issueInfo, pr.Merged, checks), itemUrl);
                }
                else
                {
                    signal = new GitHubIssueSignal(issueInfo, itemUrl);
                }

                signal.Context = existing.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
            catch
            {
                var issueInfo = new GitHubIssueInfo(
                    GetNumber(existing),
                    GetTitle(existing),
                    "Inaccessible - likely deleted or access lost",
                    DateTimeOffset.UtcNow,
                    null,
                    null);

                Signal signal;
                if (existing is GitHubPullRequestSignal existingPr)
                {
                    var info = new GitHubPullRequestInfo(issueInfo, existingPr.TypedInfo.Merged, null);
                    signal = new GitHubPullRequestSignal(info, existing.Url);
                }
                else
                {
                    signal = new GitHubIssueSignal(issueInfo, existing.Url);
                }

                signal.Context = existing.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
        }

        return signals;
    }

    internal static bool MatchesItemConfig(Issue item, GitHubItemConfig config)
    {
        if (!config.Name.IsMatch(item.Title))
        {
            return false;
        }

        if (config.Authors.Count > 0 && !MatchesAuthor(item.User?.Login, config.Authors))
        {
            return false;
        }

        if (config.Labels.Count > 0 && !config.Labels.All(cl => item.Labels.Any(l => l.Name.Equals(cl, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (config.ExcludeLabels.Count > 0 && item.Labels.Any(l => config.ExcludeLabels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static DateTimeOffset? GetUpdatedAt(Signal signal) => signal switch
    {
        GitHubPullRequestSignal pr => pr.TypedInfo.IssueInfo.UpdatedAt,
        GitHubIssueSignal issue => issue.TypedInfo.UpdatedAt,
        _ => null
    };

    private static int GetNumber(Signal signal) => signal switch
    {
        GitHubPullRequestSignal pr => pr.TypedInfo.IssueInfo.Number,
        GitHubIssueSignal issue => issue.TypedInfo.Number,
        _ => throw new InvalidOperationException($"Unexpected signal type: {signal.Type}")
    };

    private static string GetTitle(Signal signal) => signal switch
    {
        GitHubPullRequestSignal pr => pr.TypedInfo.IssueInfo.Title,
        GitHubIssueSignal issue => issue.TypedInfo.Title,
        _ => throw new InvalidOperationException($"Unexpected signal type: {signal.Type}")
    };

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

    private static bool MatchesAuthor(string? login, List<string> authors)
    {
        if (login is null)
        {
            return false;
        }

        foreach (var author in authors)
        {
            if (author.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
            {
                var appName = author[4..];
                if (string.Equals(login, $"{appName}[bot]", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(login, author, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<List<string>> GetItemCommentsAsync(RepositoryContext context, int number)
    {
        var comments = await context.Client.Issue.Comment.GetAllForIssue(
            context.Organization, context.RepositoryName, number);
        return comments.Select(c => c.Body).ToList();
    }

    private static async Task<List<GitHubCheckInfo>> GetPullRequestChecksAsync(RepositoryContext context, string sha)
    {
        var checkRuns = await context.Client.Check.Run.GetAllForReference(
            context.Organization, context.RepositoryName, sha);
        return checkRuns.CheckRuns
            .Select(cr => new GitHubCheckInfo(cr.Name, cr.Status.Value.ToString(), cr.Conclusion?.Value.ToString()))
            .ToList();
    }
}
