using System.Diagnostics;
using System.Text.Json;
using BuildDuty.Core.Models;

namespace BuildDuty.Core;

/// <summary>
/// Deterministic work item collector for GitHub issues and pull requests.
/// Uses <c>gh</c> CLI — no AI involved.
/// </summary>
public sealed class GitHubWorkItemCollector
{
    private readonly GitHubConfig _config;

    public GitHubWorkItemCollector(GitHubConfig config)
    {
        _config = config;
    }

    public async Task<CollectionResult> CollectIssuesAsync(WorkItemStore? store = null, CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var sources = new List<CollectedSource>();
        int created = 0, updated = 0, closed = 0;

        try
        {
            foreach (var org in _config.Organizations)
            {
                foreach (var repo in org.Repositories.Where(r => r.Issues is not null))
                {
                    ct.ThrowIfCancellationRequested();
                    var issues = await FetchIssuesAsync(org.Organization, repo, repo.Issues!, ct);
                    sources.AddRange(issues);

                    if (store is not null)
                    {
                        var collectedIds = new HashSet<string>();

                        foreach (var source in issues)
                        {
                            collectedIds.Add(source.Id);

                            if (!store.Exists(source.Id))
                            {
                                var metadata = source.Metadata.Count > 0 ? source.Metadata : null;
                                await store.SaveAsync(new WorkItem
                                {
                                    Id = source.Id,
                                    Status = "new",
                                    State = "new",
                                    Title = source.Title,
                                    CorrelationId = source.CorrelationId,
                                    Sources = [new SourceReference
                                    {
                                        Type = source.SourceType,
                                        Ref = source.SourceRef,
                                        SourceUpdatedAtUtc = source.SourceUpdatedAtUtc,
                                        Metadata = metadata,
                                    }],
                                });
                                created++;
                            }
                            else
                            {
                                var existing = await store.LoadAsync(source.Id);
                                if (existing is not null)
                                {
                                    var sourceRef = existing.Sources.FirstOrDefault();
                                    if (sourceRef is null) continue;

                                    var changed = false;

                                    // Check if updatedAt changed
                                    if (source.SourceUpdatedAtUtc.HasValue &&
                                        sourceRef.SourceUpdatedAtUtc != source.SourceUpdatedAtUtc)
                                    {
                                        sourceRef.SourceUpdatedAtUtc = source.SourceUpdatedAtUtc;
                                        changed = true;
                                    }

                                    // Check if linked PRs changed
                                    var newLinkedPrs = source.Metadata.GetValueOrDefault("linkedPrs") ?? "";
                                    var oldLinkedPrs = sourceRef.Metadata?.GetValueOrDefault("linkedPrs") ?? "";
                                    if (newLinkedPrs != oldLinkedPrs)
                                    {
                                        sourceRef.Metadata ??= new Dictionary<string, string>();
                                        if (newLinkedPrs.Length > 0)
                                            sourceRef.Metadata["linkedPrs"] = newLinkedPrs;
                                        else
                                            sourceRef.Metadata.Remove("linkedPrs");
                                        changed = true;
                                    }

                                    if (changed)
                                    {
                                        if (existing.State != "new")
                                            existing.State = "updated";
                                        await store.SaveAsync(existing);
                                        updated++;
                                    }
                                }
                            }
                        }

                        // Mark existing items whose source is no longer collected
                        var prefix = $"wi_gh_issue_{org.Organization}_{repo.Name}_";
                        var allItems = await store.ListAsync();
                        foreach (var item in allItems.Where(i =>
                            i.Id.StartsWith(prefix, StringComparison.Ordinal) && !i.IsResolved &&
                            !collectedIds.Contains(i.Id)))
                        {
                            item.State = "closed";
                            await store.SaveAsync(item);
                            closed++;
                        }
                    }
                }
            }

            return new CollectionResult
            {
                Source = "GitHub Issues",
                Success = true,
                Sources = sources,
                Created = created,
                Updated = updated,
                Closed = closed,
                Duration = started.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new CollectionResult
            {
                Source = "GitHub Issues",
                Success = false,
                Error = ex.Message,
                Sources = sources,
                Created = created,
                Updated = updated,
                Closed = closed,
                Duration = started.Elapsed,
            };
        }
    }

    public async Task<CollectionResult> CollectPullRequestsAsync(WorkItemStore? store = null, CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var sources = new List<CollectedSource>();
        int created = 0, updated = 0, closed = 0;

        try
        {
            foreach (var org in _config.Organizations)
            {
                foreach (var repo in org.Repositories.Where(r => r.PullRequests is { Count: > 0 }))
                {
                    ct.ThrowIfCancellationRequested();
                    var prs = await FetchPullRequestsAsync(org.Organization, repo, repo.PullRequests!, ct);
                    sources.AddRange(prs);

                    if (store is not null)
                    {
                        var collectedIds = new HashSet<string>();

                        foreach (var source in prs)
                        {
                            collectedIds.Add(source.Id);

                            if (!store.Exists(source.Id))
                            {
                                await store.SaveAsync(new WorkItem
                                {
                                    Id = source.Id,
                                    Status = "new",
                                    State = "new",
                                    Title = source.Title,
                                    CorrelationId = source.CorrelationId,
                                    Sources = [new SourceReference
                                    {
                                        Type = source.SourceType,
                                        Ref = source.SourceRef,
                                        SourceUpdatedAtUtc = source.SourceUpdatedAtUtc,
                                    }],
                                });
                                created++;
                            }
                            else if (source.SourceUpdatedAtUtc.HasValue)
                            {
                                var existing = await store.LoadAsync(source.Id);
                                if (existing is not null)
                                {
                                    var sourceRef = existing.Sources.FirstOrDefault();
                                    if (sourceRef is not null && sourceRef.SourceUpdatedAtUtc != source.SourceUpdatedAtUtc)
                                    {
                                        sourceRef.SourceUpdatedAtUtc = source.SourceUpdatedAtUtc;
                                        if (existing.State != "new")
                                            existing.State = "updated";
                                        await store.SaveAsync(existing);
                                        updated++;
                                    }
                                }
                            }
                        }

                        // Mark existing items whose source is no longer collected
                        var prefix = $"wi_gh_pr_{org.Organization}_{repo.Name}_";
                        var allItems = await store.ListAsync();
                        foreach (var item in allItems.Where(i =>
                            i.Id.StartsWith(prefix, StringComparison.Ordinal) && !i.IsResolved &&
                            !collectedIds.Contains(i.Id)))
                        {
                            item.State = "closed";
                            await store.SaveAsync(item);
                            closed++;
                        }
                    }
                }
            }

            return new CollectionResult
            {
                Source = "GitHub PRs",
                Success = true,
                Sources = sources,
                Created = created,
                Updated = updated,
                Closed = closed,
                Duration = started.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new CollectionResult
            {
                Source = "GitHub PRs",
                Success = false,
                Error = ex.Message,
                Sources = sources,
                Created = created,
                Updated = updated,
                Closed = closed,
                Duration = started.Elapsed,
            };
        }
    }

    private static async Task<List<CollectedSource>> FetchIssuesAsync(
        string owner, GitHubRepositoryConfig repo, GitHubIssueConfig config, CancellationToken ct)
    {
        var labelFilter = config.Labels.Count > 0
            ? $"--label {string.Join(",", config.Labels)}"
            : "";

        var state = config.EffectiveState;

        var output = await RunGhAsync(
            $"issue list --repo {owner}/{repo.Name} --state {state} {labelFilter} " +
            "--json number,title,state,url,updatedAt --limit 100");

        if (output is null) return [];

        var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

        // Fetch linked PRs for all issues in parallel
        var linkedPrTasks = items.Select(i =>
        {
            var number = i.GetProperty("number").GetInt32();
            return FetchLinkedPrsAsync(owner, repo.Name, number);
        }).ToList();
        var linkedPrResults = await Task.WhenAll(linkedPrTasks);

        return items.Select((i, idx) =>
        {
            var number = i.GetProperty("number").GetInt32();
            var title = i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var issueState = i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open";
            var url = i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{owner}/{repo.Name}/issues/{number}";
            var updatedAt = i.TryGetProperty("updatedAt", out var ua) && ua.TryGetDateTime(out var dt) ? dt : (DateTime?)null;

            var metadata = new Dictionary<string, string>();
            var linkedPrs = linkedPrResults[idx];
            if (linkedPrs.Count > 0)
                metadata["linkedPrs"] = string.Join(", ", linkedPrs);

            return new CollectedSource
            {
                Id = $"wi_gh_issue_{owner}_{repo.Name}_{number}",
                Title = $"[{owner}/{repo.Name}#{number}] {title}",
                CorrelationId = $"corr_gh_{owner}_{repo.Name}_issue_{number}",
                SourceType = "github-issue",
                SourceRef = url,
                Status = issueState,
                SourceUpdatedAtUtc = updatedAt?.ToUniversalTime(),
                Metadata = metadata,
            };
        }).ToList();
    }

    /// <summary>
    /// Fetches PRs cross-referenced or connected to an issue via GraphQL timeline.
    /// Returns a list of PR URLs.
    /// </summary>
    private static async Task<List<string>> FetchLinkedPrsAsync(string owner, string repo, int issueNumber)
    {
        var query = $$"""
            { repository(owner:"{{owner}}", name:"{{repo}}") {
                issue(number:{{issueNumber}}) {
                    timelineItems(last:20, itemTypes:[CROSS_REFERENCED_EVENT, CONNECTED_EVENT]) {
                        nodes {
                            __typename
                            ... on CrossReferencedEvent { source { ... on PullRequest { url state } } }
                            ... on ConnectedEvent { subject { ... on PullRequest { url state } } }
                        }
                    }
                }
            } }
            """;

        var output = await RunGhAsync($"api graphql -f query='{query.Replace("'", "'\\''")}'");
        if (output is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(output);
            var nodes = doc.RootElement
                .GetProperty("data")
                .GetProperty("repository")
                .GetProperty("issue")
                .GetProperty("timelineItems")
                .GetProperty("nodes");

            var prs = new HashSet<string>();
            foreach (var node in nodes.EnumerateArray())
            {
                // CrossReferencedEvent → source.url
                if (node.TryGetProperty("source", out var source) &&
                    source.TryGetProperty("url", out var srcUrl))
                    prs.Add(srcUrl.GetString()!);

                // ConnectedEvent → subject.url
                if (node.TryGetProperty("subject", out var subject) &&
                    subject.TryGetProperty("url", out var subUrl))
                    prs.Add(subUrl.GetString()!);
            }
            return prs.ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<CollectedSource>> FetchPullRequestsAsync(
        string owner, GitHubRepositoryConfig repo, List<GitHubPullRequestPattern> patterns,
        CancellationToken ct)
    {
        var sources = new List<CollectedSource>();

        // Group patterns by state to minimize API calls
        var byState = patterns.GroupBy(p => p.EffectiveState, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byState)
        {
            var state = group.Key;
            var statePatterns = group.ToList();

            var output = await RunGhAsync(
                $"pr list --repo {owner}/{repo.Name} --state {state} " +
                "--json number,title,state,url,updatedAt --limit 200");

            if (output is null) continue;

            var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

            var matched = items
                .Select(i => (
                    Number: i.GetProperty("number").GetInt32(),
                    Title: i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    State: i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open",
                    Url: i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{owner}/{repo.Name}/pull/{i.GetProperty("number").GetInt32()}",
                    UpdatedAt: i.TryGetProperty("updatedAt", out var ua) && ua.TryGetDateTime(out var dt) ? dt : (DateTime?)null
                ))
                .Where(pr => MatchesAnyPattern(pr.Title, statePatterns))
                .Select(pr => new CollectedSource
                {
                    Id = $"wi_gh_pr_{owner}_{repo.Name}_{pr.Number}",
                    Title = $"[{owner}/{repo.Name}#{pr.Number}] {pr.Title}",
                    CorrelationId = $"corr_gh_{owner}_{repo.Name}_pr_{pr.Number}",
                    SourceType = "github-pr",
                    SourceRef = pr.Url,
                    Status = pr.State,
                    SourceUpdatedAtUtc = pr.UpdatedAt?.ToUniversalTime(),
                });

            sources.AddRange(matched);
        }

        return sources;
    }

    /// <summary>
    /// Check if a PR title matches any of the configured name patterns.
    /// Patterns starting with <c>*</c> match as a suffix (contains).
    /// Exact patterns must match the full title.
    /// </summary>
    private static bool MatchesAnyPattern(string title, List<GitHubPullRequestPattern> patterns)
    {
        foreach (var p in patterns)
        {
            if (p.Name.StartsWith('*'))
            {
                // Wildcard prefix — match as "contains" on the remainder
                var suffix = p.Name[1..];
                if (title.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                // Exact match
                if (title.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static async Task<string?> RunGhAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("gh", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
