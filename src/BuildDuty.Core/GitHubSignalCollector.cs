using System.Diagnostics;
using System.Text.Json;
using BuildDuty.Core.Models;

namespace BuildDuty.Core;

/// <summary>
/// Deterministic signal collector for GitHub issues and pull requests.
/// Uses <c>gh</c> CLI — no AI involved.
/// </summary>
public sealed class GitHubSignalCollector
{
    private readonly GitHubConfig _config;

    public GitHubSignalCollector(GitHubConfig config)
    {
        _config = config;
    }

    public async Task<CollectionResult> CollectIssuesAsync(CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var signals = new List<CollectedSignal>();

        try
        {
            foreach (var org in _config.Organizations)
            {
                foreach (var repo in org.Repositories.Where(r => r.Issues is not null))
                {
                    ct.ThrowIfCancellationRequested();
                    var issues = await FetchIssuesAsync(org.Organization, repo, repo.Issues!, ct);
                    signals.AddRange(issues);
                }
            }

            return new CollectionResult
            {
                Source = "GitHub Issues",
                Success = true,
                Signals = signals,
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
                Signals = signals,
                Duration = started.Elapsed,
            };
        }
    }

    public async Task<CollectionResult> CollectPullRequestsAsync(CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var signals = new List<CollectedSignal>();

        try
        {
            foreach (var org in _config.Organizations)
            {
                foreach (var repo in org.Repositories.Where(r => r.PullRequests is { Count: > 0 }))
                {
                    ct.ThrowIfCancellationRequested();
                    var prs = await FetchPullRequestsAsync(org.Organization, repo, repo.PullRequests!, ct);
                    signals.AddRange(prs);
                }
            }

            return new CollectionResult
            {
                Source = "GitHub PRs",
                Success = true,
                Signals = signals,
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
                Signals = signals,
                Duration = started.Elapsed,
            };
        }
    }

    private static async Task<List<CollectedSignal>> FetchIssuesAsync(
        string owner, GitHubRepositoryConfig repo, GitHubIssueConfig config, CancellationToken ct)
    {
        var labelFilter = config.Labels.Count > 0
            ? $"--label {string.Join(",", config.Labels)}"
            : "";

        var state = config.EffectiveState;

        var output = await RunGhAsync(
            $"issue list --repo {owner}/{repo.Name} --state {state} {labelFilter} " +
            "--json number,title,state,url --limit 100");

        if (output is null) return [];

        var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

        return items.Select(i =>
        {
            var number = i.GetProperty("number").GetInt32();
            var title = i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var issueState = i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open";
            var url = i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{owner}/{repo.Name}/issues/{number}";

            return new CollectedSignal
            {
                Id = $"wi_gh_issue_{owner}_{repo.Name}_{number}",
                Title = $"[{owner}/{repo.Name}#{number}] {title}",
                CorrelationId = $"corr_gh_{owner}_{repo.Name}_issue_{number}",
                SignalType = "github-issue",
                SignalRef = url,
                Status = issueState,
            };
        }).ToList();
    }

    private static async Task<List<CollectedSignal>> FetchPullRequestsAsync(
        string owner, GitHubRepositoryConfig repo, List<GitHubPullRequestPattern> patterns,
        CancellationToken ct)
    {
        var signals = new List<CollectedSignal>();

        // Group patterns by state to minimize API calls
        var byState = patterns.GroupBy(p => p.EffectiveState, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byState)
        {
            var state = group.Key;
            var statePatterns = group.ToList();

            var output = await RunGhAsync(
                $"pr list --repo {owner}/{repo.Name} --state {state} " +
                "--json number,title,state,url --limit 200");

            if (output is null) continue;

            var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

            var matched = items
                .Select(i => (
                    Number: i.GetProperty("number").GetInt32(),
                    Title: i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    State: i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open",
                    Url: i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{owner}/{repo.Name}/pull/{i.GetProperty("number").GetInt32()}"
                ))
                .Where(pr => MatchesAnyPattern(pr.Title, statePatterns))
                .Select(pr => new CollectedSignal
                {
                    Id = $"wi_gh_pr_{owner}_{repo.Name}_{pr.Number}",
                    Title = $"[{owner}/{repo.Name}#{pr.Number}] {pr.Title}",
                    CorrelationId = $"corr_gh_{owner}_{repo.Name}_pr_{pr.Number}",
                    SignalType = "github-pr",
                    SignalRef = pr.Url,
                    Status = pr.State,
                });

            signals.AddRange(matched);
        }

        return signals;
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
