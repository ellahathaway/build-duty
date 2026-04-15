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

        var issueSignals = signals
            .Where(s => s.Type == SignalType.GitHubIssue)
            .OfType<GitHubIssueSignal>()
            .ToList();

        var prSignals = signals
            .Where(s => s.Type == SignalType.GitHubPullRequest)
            .OfType<GitHubPullRequestSignal>()
            .ToList();

        var collectedSignals = new List<Signal>();

        foreach (var org in Config.Organizations)
        {
            foreach (var repo in org.Repositories)
            {
                var context = await CreateRepositoryContextAsync(org.Organization, repo.Name);

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

    protected virtual async Task<RepositoryContext> CreateRepositoryContextAsync(string organization, string repository)
    {
        var client = await TokenProvider.GetGitHubClientAsync(organization, repository);
        return new RepositoryContext(organization, repository, client);
    }

    private static async Task<List<GitHubIssueSignal>> CollectIssueSignalsAsync(
        RepositoryContext context,
        GitHubIssueConfig? issueConfig,
        IEnumerable<GitHubIssueSignal> existingSignals)
    {
        var signals = new List<GitHubIssueSignal>();
        var seenUrls = new HashSet<Uri>();

        if (issueConfig != null)
        {
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

            foreach (var issue in issues)
            {
                if (issue.PullRequest is not null)
                {
                    continue; // GitHub API returns PRs as issues too
                }

                if (!MatchesIssueConfig(issue, issueConfig))
                {
                    continue;
                }

                seenUrls.Add(new Uri(issue.HtmlUrl));

                var existingSignal = existingSignals.FirstOrDefault(s => s.Url == new Uri(issue.HtmlUrl));
                if (existingSignal != null && existingSignal.TypedInfo.UpdatedAt == issue.UpdatedAt)
                {
                    continue;
                }

                var info = new GitHubIssueInfo(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt,
                    issue.Body,
                    await GetIssueCommentsAsync(context, issue.Number));
                var signal = new GitHubIssueSignal(info, new Uri(issue.HtmlUrl));
                signal.Context = issueConfig.Context;
                if (existingSignal is not null)
                {
                    signal.PreserveFrom(existingSignal);
                }
                signals.Add(signal);
            }
        }

        // Only re-fetch existing signals that belong to this repo.
        var repoPrefix = $"https://github.com/{context.Organization}/{context.RepositoryName}/".ToLowerInvariant();
        var missedSignals = existingSignals
            .Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => !seenUrls.Contains(s.Url));

        foreach (var existing in missedSignals)
        {
            try
            {
                var issue = await context.Client.Issue.Get(
                    context.Organization,
                    context.RepositoryName,
                    existing.TypedInfo.Number);

                if (existing.TypedInfo.UpdatedAt == issue.UpdatedAt)
                {
                    continue;
                }

                var info = new GitHubIssueInfo(issue.Number, issue.Title, issue.State.Value.ToString(), issue.UpdatedAt,
                    issue.Body,
                    await GetIssueCommentsAsync(context, issue.Number));
                var signal = new GitHubIssueSignal(info, new Uri(issue.HtmlUrl));
                signal.Context = issueConfig?.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
            catch
            {
                // Issue may have been deleted or made inaccessible - in that case we should mark the signal as updated with state "Inaccessible"
                var info = new GitHubIssueInfo(existing.TypedInfo.Number, existing.TypedInfo.Title, "Inaccessible", DateTimeOffset.UtcNow,
                    null, null);
                var signal = new GitHubIssueSignal(info, existing.Url);
                signal.Context = existing.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
        }

        return signals;
    }

    private static async Task<List<GitHubPullRequestSignal>> CollectPullRequestSignalsAsync(
        RepositoryContext context,
        List<GitHubPullRequestPattern>? prPatterns,
        IEnumerable<GitHubPullRequestSignal> existingSignals
        )
    {
        var patternsByState = prPatterns?
            .Where(pattern => pattern.Name is not null)
            .GroupBy(pattern => pattern.State)
            .ToList();

        var signals = new List<GitHubPullRequestSignal>();
        var seenUrls = new HashSet<Uri>();

        if (patternsByState != null)
        {
            foreach (var patternGroup in patternsByState)
            {
                var pulls = await context.Client.PullRequest.GetAllForRepository(
                    context.Organization, context.RepositoryName,
                    new PullRequestRequest { State = patternGroup.Key });

                var matchingPulls = pulls.Where(pr => patternGroup.Any(pattern => MatchesPullRequestPattern(pr, pattern)));

                var patternContext = patternGroup.FirstOrDefault()?.Context;

                foreach (var pr in matchingPulls)
                {
                    seenUrls.Add(new Uri(pr.HtmlUrl));

                    var info = new GitHubPullRequestInfo(pr.Number, pr.Title, pr.State.Value.ToString(), pr.UpdatedAt, pr.Merged,
                        pr.Body,
                        await GetIssueCommentsAsync(context, pr.Number),
                        await GetPullRequestChecksAsync(context, pr.Head.Sha));
                    var signal = new GitHubPullRequestSignal(info, new Uri(pr.HtmlUrl));
                    signal.Context = patternContext;
                    var existingSignal = existingSignals.FirstOrDefault(s => s.Url == new Uri(pr.HtmlUrl));

                    if (existingSignal is not null)
                    {
                        if (existingSignal.TypedInfo.UpdatedAt == pr.UpdatedAt)
                        {
                            continue;
                        }
                        signal.PreserveFrom(existingSignal);
                    }
                    signals.Add(signal);
                }
            }
        }

        // Only re-fetch existing signals that belong to this repo.
        var repoPrefix = $"https://github.com/{context.Organization}/{context.RepositoryName}/".ToLowerInvariant();
        var missedSignals = existingSignals
            .Where(s => s.Url.ToString().StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => !seenUrls.Contains(s.Url));

        foreach (var existing in missedSignals)
        {
            try
            {
                var pr = await context.Client.PullRequest.Get(
                    context.Organization,
                    context.RepositoryName,
                    existing.TypedInfo.Number);

                if (existing.TypedInfo.UpdatedAt == pr.UpdatedAt)
                {
                    continue;
                }

                var info = new GitHubPullRequestInfo(pr.Number, pr.Title, pr.State.Value.ToString(), pr.UpdatedAt, pr.Merged,
                    pr.Body,
                    await GetIssueCommentsAsync(context, pr.Number),
                    await GetPullRequestChecksAsync(context, pr.Head.Sha));
                var signal = new GitHubPullRequestSignal(info, new Uri(pr.HtmlUrl));
                signal.Context = existing.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
            catch
            {
                // PR may have been deleted or made inaccessible. In that case we should mark the signal as updated with state "Inaccessible"
                var info = new GitHubPullRequestInfo(existing.TypedInfo.Number, existing.TypedInfo.Title, "Inaccessible", DateTimeOffset.UtcNow, existing.TypedInfo.Merged,
                    null, null, null);
                var signal = new GitHubPullRequestSignal(info, existing.Url);
                signal.Context = existing.Context;
                signal.PreserveFrom(existing);
                signals.Add(signal);
            }
        }

        return signals;
    }

    internal static bool MatchesIssueConfig(Issue issue, GitHubIssueConfig config)
    {
        if (config.Authors.Count > 0 && !MatchesAuthor(issue.User?.Login, config.Authors))
        {
            return false;
        }

        if (config.ExcludeLabels.Count > 0 && issue.Labels.Any(l => config.ExcludeLabels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
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

    internal static bool MatchesPullRequestPattern(PullRequest pr, GitHubPullRequestPattern pattern)
    {
        if (!pattern.Name.IsMatch(pr.Title))
        {
            return false;
        }

        if (pattern.Authors.Count > 0 && !MatchesAuthor(pr.User?.Login, pattern.Authors))
        {
            return false;
        }

        if (pattern.Labels.Count > 0 && !pr.Labels.Any(l => pattern.Labels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (pattern.ExcludeLabels.Count > 0 && pr.Labels.Any(l => pattern.ExcludeLabels.Contains(l.Name, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
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

    private static async Task<List<string>> GetIssueCommentsAsync(RepositoryContext context, int number)
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
