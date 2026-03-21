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
            foreach (var repo in _config.Repositories.Where(r => r.Issues is not null))
            {
                ct.ThrowIfCancellationRequested();
                var issues = await FetchIssuesAsync(repo, repo.Issues!, ct);
                signals.AddRange(issues);
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
            foreach (var repo in _config.Repositories.Where(r => r.PullRequests is not null))
            {
                ct.ThrowIfCancellationRequested();
                var prs = await FetchPullRequestsAsync(repo, repo.PullRequests!, ct);
                signals.AddRange(prs);
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
        GitHubRepositoryConfig repo, GitHubIssueConfig config, CancellationToken ct)
    {
        var labelFilter = config.Labels.Count > 0
            ? $"--label {string.Join(",", config.Labels)}"
            : "";

        var state = config.EffectiveState;

        var output = await RunGhAsync(
            $"issue list --repo {repo.Owner}/{repo.Name} --state {state} {labelFilter} " +
            "--json number,title,state,url --limit 100");

        if (output is null) return [];

        var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

        return items.Select(i =>
        {
            var number = i.GetProperty("number").GetInt32();
            var title = i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var issueState = i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open";
            var url = i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{repo.Owner}/{repo.Name}/issues/{number}";

            return new CollectedSignal
            {
                Id = $"wi_gh_issue_{repo.Owner}_{repo.Name}_{number}",
                Title = $"[{repo.Owner}/{repo.Name}#{number}] {title}",
                CorrelationId = $"corr_gh_{repo.Owner}_{repo.Name}_issue_{number}",
                SignalType = "github-issue",
                SignalRef = url,
                Status = issueState,
            };
        }).ToList();
    }

    private static async Task<List<CollectedSignal>> FetchPullRequestsAsync(
        GitHubRepositoryConfig repo, GitHubPullRequestConfig config, CancellationToken ct)
    {
        var labelFilter = config.Labels.Count > 0
            ? $"--label {string.Join(",", config.Labels)}"
            : "";

        var state = config.EffectiveState;

        var output = await RunGhAsync(
            $"pr list --repo {repo.Owner}/{repo.Name} --state {state} {labelFilter} " +
            "--json number,title,state,url,isDraft --limit 100");

        if (output is null) return [];

        var items = JsonSerializer.Deserialize<List<JsonElement>>(output) ?? [];

        return items.Select(i =>
        {
            var number = i.GetProperty("number").GetInt32();
            var title = i.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var prState = i.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open";
            var url = i.TryGetProperty("url", out var u) ? u.GetString() ?? "" : $"https://github.com/{repo.Owner}/{repo.Name}/pull/{number}";

            return new CollectedSignal
            {
                Id = $"wi_gh_pr_{repo.Owner}_{repo.Name}_{number}",
                Title = $"[{repo.Owner}/{repo.Name}#{number}] {title}",
                CorrelationId = $"corr_gh_{repo.Owner}_{repo.Name}_pr_{number}",
                SignalType = "github-pr",
                SignalRef = url,
                Status = prState,
            };
        }).ToList();
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
