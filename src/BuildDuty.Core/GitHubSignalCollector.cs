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
        var signals = await StorageProvider.GetAllSignalsAsync();
        var issueSignals = signals.OfType<GitHubIssueSignal>();
        var prSignals = signals.OfType<GitHubPullRequestSignal>();

        // Group all configs and existing signals by org/repo so we collect
        // once per repo and only gather missed signals a single time.
        var repoGroups = Config.Organizations
            .SelectMany(org => org.Repositories.Select(repo => (Org: org.Organization, Repo: repo)))
            .GroupBy(x => (x.Org, x.Repo.Name))
            .Select(g =>
            {
                var org = g.Key.Org;
                var repoName = g.Key.Name;
                var issueConfigs = g.SelectMany(x => x.Repo.Issues ?? []).ToList();
                var prConfigs = g.SelectMany(x => x.Repo.PullRequests ?? []).ToList();
                var repoPrefix = $"https://github.com/{org}/{repoName}/";
                var issueSignalsForRepo = issueSignals
                    .Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var prSignalsForRepo = prSignals
                    .Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return (Org: org, RepoName: repoName, IssueConfigs: issueConfigs, PrConfigs: prConfigs, IssueSignals: issueSignalsForRepo, PrSignals: prSignalsForRepo);
            });

        var collectedSignals = new List<Signal>();

        var repoTasks = repoGroups.Select(async group =>
        {
            var (org, repoName, issueConfigs, prConfigs, issueSignalsForRepo, prSignalsForRepo) = group;
            var context = await CreateRepositoryContextAsync(org, repoName);

            var issueSignals = await CollectSignalsAsync(context, issueConfigs, issueSignalsForRepo, isPullRequest: false);
            var prSignals = await CollectSignalsAsync(context, prConfigs, prSignalsForRepo, isPullRequest: true);
            return issueSignals.Concat(prSignals).ToList();
        });

        var results = await Task.WhenAll(repoTasks);
        foreach (var newSignals in results)
        {
            collectedSignals.AddRange(newSignals);
        }

        return collectedSignals;
    }

    protected virtual async Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
    {
        var client = await TokenProvider.GetGitHubClientAsync(organization, repository);
        return new RepositoryContext(organization, repository, client);
    }

    private static async Task<List<Signal>> CollectSignalsAsync(
        RepositoryContext context,
        IEnumerable<GitHubItemConfig> itemConfigs,
        IEnumerable<Signal> existingSignals,
        bool isPullRequest)
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
                .Where(i => (i.PullRequest != null) == isPullRequest)
                .Where(i => MatchesItemConfig(i, itemConfig));

            foreach (var item in items)
            {
                var itemUrl = new Uri(item.HtmlUrl);
                seenUrls.Add(itemUrl);

                var existingSignal = existingSignals.FirstOrDefault(s => s.Url == itemUrl);
                if (existingSignal != null)
                {
                    var updatedAt = existingSignal is GitHubIssueSignal issueSignal ? issueSignal.TypedInfo.ItemInfo.UpdatedAt :
                        existingSignal is GitHubPullRequestSignal prSignal ? prSignal.TypedInfo.ItemInfo.UpdatedAt :
                        throw new InvalidOperationException("Unexpected signal type");

                    if (updatedAt == item.UpdatedAt)
                    {
                        continue;
                    }
                }

                var signal = await CreateSignalAsync(context, item, new Uri(item.HtmlUrl), isPullRequest);
                signal.Context = itemConfig.Context;
                if (existingSignal is not null)
                {
                    signal.PreserveFrom(existingSignal);
                }
                signals.Add(signal);
            }
        }

        signals.AddRange(await RefetchMissedSignalsAsync(context, existingSignals, seenUrls, isPullRequest));
        return signals;
    }

    /// <summary>
    /// Re-fetches existing signals that weren't returned by the config-based queries.
    /// These may have changed state (e.g. closed) or become inaccessible (deleted/permissions).
    /// </summary>
    private static async Task<List<Signal>> RefetchMissedSignalsAsync(
        RepositoryContext context,
        IEnumerable<Signal> existingSignals,
        HashSet<Uri> seenUrls,
        bool isPullRequest)
    {
        var signals = new List<Signal>();

        foreach (var existing in existingSignals.Where(s => !seenUrls.Contains(s.Url)))
        {
            try
            {
                var issueInfo = existing is GitHubIssueSignal issueSignal ? issueSignal.TypedInfo.ItemInfo :
                    existing is GitHubPullRequestSignal prSignal ? prSignal.TypedInfo.ItemInfo :
                    throw new InvalidOperationException("Unexpected signal type");

                var item = await context.Client.Issue.Get(
                    context.Organization,
                    context.RepositoryName,
                    issueInfo.Number);

                if (issueInfo.UpdatedAt == item.UpdatedAt)
                {
                    continue;
                }

                var signal = await CreateSignalAsync(context, item, new Uri(item.HtmlUrl), isPullRequest);
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
            catch
            {
                var signal = CreateInaccessibleSignal(existing);
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
        }

        return signals;
    }

    private static async Task<Signal> CreateSignalAsync(RepositoryContext context, Issue item, Uri url, bool isPullRequest)
    {
        if (isPullRequest)
        {
            var prInfo = await CreateGitHubPullRequestInfoAsync(context, item);
            return new GitHubPullRequestSignal(prInfo, url);
        }

        var issueInfo = await CreateGitHubIssueInfoAsync(context, item);
        return new GitHubIssueSignal(issueInfo, url);
    }

    private static Signal CreateInaccessibleSignal(Signal existing)
    {
        if (existing is GitHubPullRequestSignal prSignal)
        {
            var prItemInfo = new GitHubItemInfo(
                prSignal.TypedInfo.ItemInfo.Number,
                prSignal.TypedInfo.ItemInfo.Title,
                "Inaccessible - likely deleted or access lost",
                DateTimeOffset.UtcNow,
                null,
                null);

            return new GitHubPullRequestSignal(
                new GitHubPullRequestInfo(prItemInfo, prSignal.TypedInfo.Merged, null), existing.Url);
        }

        if (existing is not GitHubIssueSignal)
        {
            throw new InvalidOperationException("Unexpected signal type");
        }

        var existingIssueInfo = ((GitHubIssueSignal)existing).TypedInfo;

        var issueInfo = new GitHubIssueInfo(
            new GitHubItemInfo(
                existingIssueInfo.ItemInfo.Number,
                existingIssueInfo.ItemInfo.Title,
                "Inaccessible - likely deleted or access lost",
                DateTimeOffset.UtcNow,
                null,
                null));

        return new GitHubIssueSignal(issueInfo, existing.Url);
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

    private static async Task<GitHubIssueInfo> CreateGitHubIssueInfoAsync(RepositoryContext context, Issue issue)
    {
        var commentsQuery = await context.Client.Issue.Comment.GetAllForIssue(context.Organization, context.RepositoryName, issue.Number);
        var comments = commentsQuery.Select(c => c.Body).ToList();

        List<GitHubTimelineEvent>? timelineEvents = null;
        try
        {
            var timeline = await context.Client.Issue.Timeline.GetAllForIssue(
                context.Organization, context.RepositoryName, issue.Number);

            timelineEvents = timeline
                .Where(e => (e.Event == EventInfoState.Crossreferenced || e.Event == EventInfoState.Connected)
                    && e.Source?.Issue?.PullRequest != null)
                .Select(e => new GitHubTimelineEvent(
                    Event: e.Event.StringValue,
                    SourceUrl: e.Source?.Issue?.HtmlUrl?.ToString(),
                    SourceState: e.Source?.Issue?.State.StringValue))
                .ToList();
        }
        catch
        {
            // Timeline may be unavailable — events will be null
        }

        return new GitHubIssueInfo(new GitHubItemInfo(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt, issue.Body, comments), timelineEvents);
    }

    private static async Task<GitHubItemInfo> CreateGitHubItemInfoAsync(RepositoryContext context, Issue issue)
    {
        var commentsQuery = await context.Client.Issue.Comment.GetAllForIssue(context.Organization, context.RepositoryName, issue.Number);
        var comments = commentsQuery.Select(c => c.Body).ToList();
        return new GitHubItemInfo(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt, issue.Body, comments);
    }

    private static async Task<GitHubPullRequestInfo> CreateGitHubPullRequestInfoAsync(RepositoryContext context, Issue issue)
    {
        var pr = await context.Client.PullRequest.Get(context.Organization, context.RepositoryName, issue.Number);
        var checkRuns = await context.Client.Check.Run.GetAllForReference(
            context.Organization, context.RepositoryName, pr.Head.Sha);

        var checks = checkRuns.CheckRuns
            .Select(cr => new GitHubCheckInfo(cr.Name, cr.Status.Value.ToString(), cr.Conclusion?.Value.ToString()))
            .ToList();

        var itemInfo = await CreateGitHubItemInfoAsync(context, issue);

        return new GitHubPullRequestInfo(itemInfo, pr.Merged, checks);
    }

}
