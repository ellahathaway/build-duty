using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Resolves active release branches for ADO pipelines by:
/// 1. Looking up each pipeline's repository via the Build API
/// 2. Listing branches in that repo via the Git API
/// 3. Filtering to branches matching active .NET release channels
///
/// Thread-safe with per-key locking — concurrent callers for the same
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
        new(@"^refs/heads/((internal/)?release/((\d+)\.(\d+)\.(\d+)xx)(-.+)?)$", RegexOptions.Compiled);

    // Suffix sort key regex — extracts type and number from suffixes like -preview3, -rc1
    private static readonly Regex SuffixPattern =
        new(@"^-(preview|rc)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        VssConnection connection, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        var org = connection.Uri.GetLeftPart(UriPartial.Authority);
        var cacheKey = $"{org}|{project}|{pipelineId}|{supportPhases}|{minVersion}";
        return _resolvedCache.GetOrAdd(cacheKey,
            _ => new Lazy<Task<List<string>>>(() =>
                ResolveInternalAsync(connection, project, pipelineId, supportPhases, minVersion))).Value;
    }

    private async Task<List<string>> ResolveInternalAsync(
        VssConnection connection, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        var org = connection.Uri.GetLeftPart(UriPartial.Authority);

        // Step 1: Get the pipeline's repository name (cached + locked per pipeline)
        var pipelineKey = $"{org}|{project}|{pipelineId}";
        var repoName = await _pipelineRepoCache.GetOrAdd(pipelineKey,
            _ => new Lazy<Task<string?>>(() => FetchPipelineRepoAsync(connection, project, pipelineId))).Value;

        if (string.IsNullOrWhiteSpace(repoName))
        {
            return ["main"];
        }

        // Step 2: List branches (cached + locked per repo)
        var repoKey = $"{org}|{project}|{repoName}";
        var rawBranches = await _repoBranchCache.GetOrAdd(repoKey,
            _ => new Lazy<Task<List<string>?>>(() => FetchRepoBranchesAsync(connection, project, repoName))).Value;

        if (rawBranches is null)
        {
            return ["main"];
        }

        // Step 3: Get supported channels from releases index (cached globally)
        var supportedChannels = GetSupportedChannels(await _releasesIndex.Value, supportPhases, minVersion);
        if (supportedChannels is null)
        {
            return ["main"];
        }

        // Step 4: Filter branches to supported channels, then for each SDK band
        // (e.g. 11.0.1xx) keep only the latest suffix (preview3 > preview2 > preview1).
        // Different bands (10.0.1xx vs 10.0.2xx) are independent.
        var candidates = new Dictionary<string, (string BranchName, string? Suffix)>();

        foreach (var refName in rawBranches)
        {
            var match = ReleaseBranchPattern.Match(refName);
            if (!match.Success)
            {
                continue;
            }

            var branchName = match.Groups[1].Value;       // e.g. release/11.0.1xx-preview3
            var isInternal = match.Groups[2].Success;      // internal/ prefix?
            var bandBase = match.Groups[3].Value;           // e.g. 11.0.1xx
            var channel = $"{match.Groups[4].Value}.{match.Groups[5].Value}"; // e.g. 11.0
            var suffix = match.Groups[7].Success ? match.Groups[7].Value : null; // e.g. -preview3

            if (!supportedChannels.Contains(channel))
            {
                continue;
            }

            // Group key: internal vs public + SDK band (e.g. "internal/11.0.1xx" or "11.0.1xx")
            var groupKey = isInternal ? $"internal/{bandBase}" : bandBase;

            if (!candidates.TryGetValue(groupKey, out var existing) ||
                CompareSuffix(suffix, existing.Suffix) > 0)
            {
                candidates[groupKey] = (branchName, suffix);
            }
        }

        var branches = new List<string>(candidates.Count + 1) { "main" };
        branches.AddRange(candidates.Values.Select(c => c.BranchName));

        return branches;
    }

    /// <summary>
    /// Compares two branch suffixes for recency. Returns positive if <paramref name="a"/>
    /// is newer, negative if older, zero if equal.
    /// Order: (no suffix/GA) > rc{N} > go-live{N} > preview{N}. Higher N wins within a type.
    /// </summary>
    internal static int CompareSuffix(string? a, string? b)
    {
        return GetSuffixSortKey(a).CompareTo(GetSuffixSortKey(b));

        static (int TypeOrder, int Number) GetSuffixSortKey(string? suffix)
        {
            if (suffix is null)
            {
                return (3, 0); // GA — highest
            }

            var match = SuffixPattern.Match(suffix);
            if (!match.Success)
            {
                return (-1, 0); // Unknown suffix — lowest
            }

            var type = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "rc" => 2,
                "go-live" => 1,
                "preview" => 0,
                _ => -1,
            };
            var number = int.Parse(match.Groups[2].Value);
            return (type, number);
        }
    }

    private static async Task<string?> FetchPipelineRepoAsync(VssConnection connection, string project, int pipelineId)
    {
        try
        {
            var buildClient = connection.GetClient<BuildHttpClient>();
            var definition = await buildClient.GetDefinitionAsync(project, pipelineId);
            return definition?.Repository?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<string>?> FetchRepoBranchesAsync(VssConnection connection, string project, string repoName)
    {
        try
        {
            var gitClient = connection.GetClient<GitHttpClient>();

            var releaseTask = gitClient.GetRefsAsync(project, repoName, filter: "heads/release/");
            var internalTask = gitClient.GetRefsAsync(project, repoName, filter: "heads/internal/release/");

            await Task.WhenAll(releaseTask, internalTask);

            var branches = new List<string>();
            foreach (var gitRef in await releaseTask)
            {
                branches.Add(gitRef.Name);
            }
            foreach (var gitRef in await internalTask)
            {
                branches.Add(gitRef.Name);
            }

            return branches;
        }
        catch
        {
            return null;
        }
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
        if (entries is null)
        {
            return null;
        }

        var phases = DefaultPhases;
        if (!string.IsNullOrWhiteSpace(supportPhases))
        {
            phases = supportPhases.Split(',').Select(p => p.Trim().ToLowerInvariant()).ToHashSet();
        }

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
