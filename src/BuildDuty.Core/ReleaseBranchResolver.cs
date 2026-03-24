using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BuildDuty.Core;

/// <summary>
/// Resolves active release branches for ADO pipelines by:
/// 1. Looking up each pipeline's repository via az CLI
/// 2. Listing branches in that repo via az CLI
/// 3. Filtering to branches matching active .NET release channels
///
/// Thread-safe with SemaphoreSlim locking — concurrent callers for the same
/// resource will wait for the first caller to populate the cache.
/// Register as a singleton in DI.
/// </summary>
public sealed class ReleaseBranchResolver
{
    private const string ReleasesIndexUrl =
        "https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json";

    private static readonly HashSet<string> DefaultPhases =
        ["active", "maintenance", "preview", "go-live", "rc"];

    private static readonly Regex ReleaseBranchPattern =
        new(@"^refs/heads/((internal/)?release/(\d+)\.(\d+)\.\d+xx)$", RegexOptions.Compiled);

    // Caches with per-key locking
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pipelineRepoCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>?>>> _repoBranchCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>>>> _resolvedCache = new();

    private readonly Lazy<Task<List<IndexEntry>?>> _releasesIndex;

    public ReleaseBranchResolver()
    {
        _releasesIndex = new Lazy<Task<List<IndexEntry>?>>(FetchReleasesIndexAsync);
    }

    /// <summary>
    /// Resolves branches for a pipeline. Returns a list of branch names.
    /// Results are cached per pipeline config — concurrent calls for the
    /// same pipeline will share a single resolution.
    /// </summary>
    public Task<List<string>> ResolveAsync(
        string org, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        if (!org.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            org = $"https://dev.azure.com/{org}";

        var cacheKey = $"{org}|{project}|{pipelineId}|{supportPhases}|{minVersion}";
        return _resolvedCache.GetOrAdd(cacheKey,
            _ => new Lazy<Task<List<string>>>(() =>
                ResolveInternalAsync(org, project, pipelineId, supportPhases, minVersion))).Value;
    }

    private async Task<List<string>> ResolveInternalAsync(
        string org, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        // Step 1: Get the pipeline's repository name (cached + locked per pipeline)
        var pipelineKey = $"{org}|{project}|{pipelineId}";
        var repoName = await _pipelineRepoCache.GetOrAdd(pipelineKey,
            _ => new Lazy<Task<string?>>(() => FetchPipelineRepoAsync(org, project, pipelineId))).Value;

        if (string.IsNullOrWhiteSpace(repoName))
            return ["main"];

        // Step 2: List branches (cached + locked per repo)
        var repoKey = $"{org}|{project}|{repoName}";
        var rawBranches = await _repoBranchCache.GetOrAdd(repoKey,
            _ => new Lazy<Task<List<string>?>>(() => FetchRepoBranchesAsync(org, project, repoName))).Value;

        if (rawBranches is null)
            return ["main"];

        // Step 3: Get supported channels from releases index (cached globally)
        var supportedChannels = GetSupportedChannels(await _releasesIndex.Value, supportPhases, minVersion);
        if (supportedChannels is null)
            return ["main"];

        // Step 4: Filter branches
        var branches = new List<string> { "main" };

        foreach (var line in rawBranches)
        {
            var match = ReleaseBranchPattern.Match(line);
            if (!match.Success) continue;

            var branchName = match.Groups[1].Value;
            var channel = $"{match.Groups[3].Value}.{match.Groups[4].Value}";

            if (supportedChannels.Contains(channel))
                branches.Add(branchName);
        }

        return branches;
    }

    private static async Task<string?> FetchPipelineRepoAsync(string org, string project, int pipelineId)
    {
        var output = await RunAzAsync(
            $"pipelines show --id {pipelineId} --org {org} --project {project} " +
            "--query repository.name -o tsv");
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static async Task<List<string>?> FetchRepoBranchesAsync(string org, string project, string repoName)
    {
        var releaseTask = RunAzAsync(
            $"repos ref list --repository {repoName} --org {org} --project {project} " +
            "--filter heads/release/ --query [].name -o tsv");
        var internalTask = RunAzAsync(
            $"repos ref list --repository {repoName} --org {org} --project {project} " +
            "--filter heads/internal/release/ --query [].name -o tsv");

        await Task.WhenAll(releaseTask, internalTask);

        var releaseOutput = await releaseTask;
        var internalOutput = await internalTask;

        if (releaseOutput is null && internalOutput is null)
            return null;

        var branches = new List<string>();
        if (releaseOutput is not null)
            branches.AddRange(releaseOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        if (internalOutput is not null)
            branches.AddRange(internalOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        return branches;
    }

    private static async Task<List<IndexEntry>?> FetchReleasesIndexAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var indexData = await http.GetFromJsonAsync<ReleasesIndex>(ReleasesIndexUrl);
            return indexData?.Entries;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<string>? GetSupportedChannels(
        List<IndexEntry>? entries, string? supportPhases, int? minVersion)
    {
        if (entries is null) return null;

        var phases = DefaultPhases;
        if (!string.IsNullOrWhiteSpace(supportPhases))
            phases = supportPhases.Split(',').Select(p => p.Trim().ToLowerInvariant()).ToHashSet();

        return entries
            .Where(e => !string.IsNullOrEmpty(e.SupportPhase) && !string.IsNullOrEmpty(e.ChannelVersion))
            .Where(e => phases.Contains(e.SupportPhase!.Trim().ToLowerInvariant()))
            .Where(e =>
            {
                var parts = e.ChannelVersion!.Split('.');
                return parts.Length >= 2 && int.TryParse(parts[0], out var major)
                    && (minVersion is null || major >= minVersion);
            })
            .Select(e => e.ChannelVersion!)
            .ToHashSet();
    }

    private static async Task<string?> RunAzAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("az", arguments)
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

    // JSON models
    private sealed class ReleasesIndex
    {
        [JsonPropertyName("releases-index")]
        public List<IndexEntry>? Entries { get; set; }
    }

    internal sealed class IndexEntry
    {
        [JsonPropertyName("channel-version")]
        public string? ChannelVersion { get; set; }

        [JsonPropertyName("support-phase")]
        public string? SupportPhase { get; set; }
    }
}
