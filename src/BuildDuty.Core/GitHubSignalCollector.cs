using System.Text.Json;
using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Octokit;

namespace BuildDuty.Core;

public class GitHubSignalCollector : SignalCollector<GitHubConfig>
{
    private readonly HashSet<string> _validExistingSignals = new();
    private readonly object _validExistingSignalsLock = new();

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
        var signals = (await StorageProvider.GetUnresolvedSignalsAsync())
            .Where(s => s is GitHubSignal);

        var collectedSignals = new List<Signal>();

        // Collect across all repositories in parallel
        var repoTasks = Config.Organizations.SelectMany(org =>
            org.Repositories.Select(repo => CollectRepositoryAsync(org.Organization, repo, signals)));

        foreach (var repoSignals in await Task.WhenAll(repoTasks))
        {
            collectedSignals.AddRange(repoSignals);
        }

        // Any existing signal not visited during collection is out of scope
        foreach (var signal in signals)
        {
            if (!_validExistingSignals.Contains(signal.Id))
            {
                var existing = signals.First(s => s.Id == signal.Id);
                existing.AsOutOfScope();

                collectedSignals.Add(existing);
            }
        }

        return collectedSignals;
    }

    private async Task<List<GitHubSignal>> CollectRepositoryAsync(
        string organization, GitHubRepositoryConfig repo, IEnumerable<Signal> signals)
    {
        var context = await CreateRepositoryContextAsync(organization, repo.Name);
        string repoPrefix = $"https://github.com/{organization}/{repo.Name}/";

        var issueSignalsTask = CollectSignalsAsync(
            context,
            repo.Issues ?? Enumerable.Empty<GitHubItemConfig>(),
            signals.OfType<GitHubIssueSignal>().Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)),
            isPullRequest: false);
        var prSignalsTask = CollectSignalsAsync(
            context,
            repo.PullRequests ?? Enumerable.Empty<GitHubItemConfig>(),
            signals.OfType<GitHubPullRequestSignal>().Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)),
            isPullRequest: true);

        var results = await Task.WhenAll(issueSignalsTask, prSignalsTask);
        return results.SelectMany(r => r).ToList();
    }

    protected virtual async Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
    {
        var client = await TokenProvider.GetGitHubClientAsync(organization, repository);
        return new RepositoryContext(organization, repository, client);
    }

    private async Task<List<GitHubSignal>> CollectSignalsAsync(
        RepositoryContext context,
        IEnumerable<GitHubItemConfig> itemConfigs,
        IEnumerable<GitHubSignal> existingSignals,
        bool isPullRequest)
    {
        var signals = new List<GitHubSignal>();
        var seenUrls = new HashSet<Uri>();

        foreach (var itemConfig in itemConfigs)
        {
            var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };

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

                var existing = existingSignals.FirstOrDefault(s => s.Url == itemUrl);
                if (existing != null)
                {
                    lock (_validExistingSignals)
                    {
                        _validExistingSignals.Add(existing.Id);
                    }

                    if (existing.ItemInfo.UpdatedAt == item.UpdatedAt)
                    {
                        continue;
                    }

                    var (comments, timelineEvents, extras) = await FetchItemDetailsAsync(context, item, isPullRequest);

                    existing.AsUpdated(item, comments, timelineEvents, extras, itemUrl);
                    signals.Add(existing);
                }
                else
                {
                    var (comments, timelineEvents, extras) = await FetchItemDetailsAsync(context, item, isPullRequest);
                    var signal = GitHubSignal.Create(item, comments, timelineEvents, extras, itemUrl, itemConfig.Context);
                    signals.Add(signal);
                }
            }
        }

        // Re-fetch existing signals not returned by queries — may have changed state or become inaccessible
        var staleTasks = existingSignals
            .Where(s => !seenUrls.Contains(s.Url))
            .Select(existing => RefreshExistingSignalAsync(context, existing));

        foreach (var result in await Task.WhenAll(staleTasks))
        {
            if (result is not null)
            {
                signals.Add(result);
            }
        }

        return signals;
    }

    /// <summary>
    /// Fetch comments, timeline events, and extras for a GitHub item in parallel.
    /// </summary>
    private static async Task<(List<string>? Comments, List<GitHubTimelineEvent>? TimelineEvents, JsonElement? Extras)>
        FetchItemDetailsAsync(RepositoryContext context, Issue item, bool isPullRequest)
    {
        var commentsTask = GetCommentsAsync(context, item);
        var timelineTask = GetTimelineEventsAsync(context, item);
        var extrasTask = FetchExtrasAsync(context, item, isPullRequest);

        await Task.WhenAll(commentsTask, timelineTask, extrasTask);

        return (await commentsTask, await timelineTask, await extrasTask);
    }

    /// <summary>
    /// Re-check a single existing signal that wasn't returned by the query.
    /// Returns the updated signal, or null if unchanged.
    /// </summary>
    private async Task<GitHubSignal?> RefreshExistingSignalAsync(RepositoryContext context, GitHubSignal existing)
    {
        lock (_validExistingSignals)
        {
            _validExistingSignals.Add(existing.Id);
        }

        try
        {
            var item = await context.Client.Issue.Get(
                context.Organization, context.RepositoryName, existing.ItemInfo.Number);

            if (existing.ItemInfo.UpdatedAt == item.UpdatedAt)
            {
                return null;
            }

            var itemUrl = new Uri(item.HtmlUrl);
            var (comments, timelineEvents, extras) = await FetchItemDetailsAsync(context, item, existing is GitHubPullRequestSignal);

            if (item.State.Value == ItemState.Closed)
            {
                existing.AsResolved(item, comments, timelineEvents, extras, itemUrl);
            }
            else
            {
                existing.AsUpdated(item, comments, timelineEvents, extras, itemUrl);
            }

            return existing;
        }
        catch
        {
            existing.AsNotFound(existing.Context);
            return existing;
        }
    }

    private static async Task<List<GitHubTimelineEvent>?> GetTimelineEventsAsync(RepositoryContext context, Issue issue)
    {
        try
        {
            var timeline = await context.Client.Issue.Timeline.GetAllForIssue(
                context.Organization, context.RepositoryName, issue.Number);

            return timeline
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
            return null;
        }
    }

    private static async Task<List<string>?> GetCommentsAsync(RepositoryContext context, Issue issue)
        => (await context.Client.Issue.Comment.GetAllForIssue(
            context.Organization, context.RepositoryName, issue.Number))
            .Select(c => c.Body).ToList();

    private static async Task<JsonElement?> FetchExtrasAsync(RepositoryContext context, Issue issue, bool isPullRequest)
    {
        if (isPullRequest)
        {
            var pr = await context.Client.PullRequest.Get(context.Organization, context.RepositoryName, issue.Number);
            var checkRuns = await context.Client.Check.Run.GetAllForReference(
                context.Organization, context.RepositoryName, pr.Head.Sha);

            var checks = checkRuns.CheckRuns
                .Select(cr => new GitHubCheckInfo(cr.Name, cr.Status.Value.ToString(), cr.Conclusion?.Value.ToString()))
                .ToList();

            return JsonSerializer.SerializeToElement(new { Merged = pr.Merged, Checks = checks });
        }

        return null;
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
}
