using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dotnet.Release;
using Dotnet.Release.Releases;
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

    private static readonly HashSet<SupportPhase> DefaultPhases =
        [SupportPhase.Active, SupportPhase.Maintenance, SupportPhase.Preview, SupportPhase.GoLive];

    private static readonly Regex ReleaseBranchPattern =
        new(@"^refs/heads/((internal/)?release/((\d+)\.(\d+)\.(\d+)(xx)?)(-.+)?)$", RegexOptions.Compiled);

    // Suffix sort key regex — extracts type and number from suffixes like -preview3, -rc1
    private static readonly Regex SuffixPattern =
        new(@"^-(preview|rc)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Parses latest-sdk versions like "11.0.100-preview.3.26207.106" or "10.0.203"
    private static readonly Regex SdkVersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)(?:-(preview|rc)\.(\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Caches with per-key locking
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pipelineRepoCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>?>>> _repoBranchCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>>>> _resolvedCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>?>>> _channelSdksCache = new();

    private readonly Lazy<Task<IList<MajorReleaseIndexItem>?>> _releasesIndex;

    public ReleaseBranchResolver()
    {
        _releasesIndex = new Lazy<Task<IList<MajorReleaseIndexItem>?>>(FetchReleasesIndexAsync);
    }

    /// <summary>
    /// Resolves branches for a pipeline. Returns a list of branch names.
    /// Results are cached per pipeline config — concurrent calls for the
    /// same pipeline will share a single resolution.
    /// </summary>
    public Task<List<string>> ResolveAsync(
        VssConnection connection, string project, int pipelineId,
        IReadOnlyCollection<SupportPhase>? supportPhases, int? minVersion)
    {
        var org = connection.Uri.GetLeftPart(UriPartial.Authority);
        var phaseKey = supportPhases is { Count: > 0 }
            ? string.Join(',', supportPhases.OrderBy(p => p))
            : "";
        var cacheKey = $"{org}|{project}|{pipelineId}|{phaseKey}|{minVersion}";
        return _resolvedCache.GetOrAdd(cacheKey,
            _ => new Lazy<Task<List<string>>>(() =>
                ResolveInternalAsync(connection, project, pipelineId, supportPhases, minVersion))).Value;
    }

    private async Task<List<string>> ResolveInternalAsync(
        VssConnection connection, string project, int pipelineId,
        IReadOnlyCollection<SupportPhase>? supportPhases, int? minVersion)
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
        var index = await _releasesIndex.Value;
        var supportedChannels = GetSupportedChannels(index, supportPhases, minVersion);
        if (supportedChannels is null)
        {
            return ["main"];
        }

        // Step 4: Filter branches to supported channels, excluding just-released branches
        var channelSdks = await FetchAllChannelSdksAsync(index, supportedChannels);
        var releasedBranches = GetReleasedBranches(index, supportedChannels, channelSdks);
        return FilterBranches(rawBranches, supportedChannels, releasedBranches);
    }

    /// <summary>
    /// Filters raw branch refs to supported channels, keeping only the latest suffix
    /// per SDK band (e.g. preview3 > preview2 > preview1). Excludes branches
    /// that match a just-released SDK version.
    /// Different bands (10.0.1xx vs 10.0.2xx) are independent.
    /// Always includes "main" as the first branch.
    /// </summary>
    internal static List<string> FilterBranches(
        List<string> rawBranches,
        HashSet<string> supportedChannels,
        HashSet<(string VersionBase, string? Suffix)>? releasedBranches = null)
    {
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
            var versionBase = match.Groups[3].Value;        // e.g. 11.0.1xx or 10.0.303
            var channel = $"{match.Groups[4].Value}.{match.Groups[5].Value}"; // e.g. 11.0
            var isBand = match.Groups[7].Success;           // true for Nxx branches
            var suffix = match.Groups[8].Success ? match.Groups[8].Value : null; // e.g. -preview3

            if (!supportedChannels.Contains(channel))
            {
                continue;
            }

            // Skip branches that match the just-released SDK version
            if (releasedBranches?.Contains((versionBase, suffix)) == true)
            {
                continue;
            }

            // Group key: internal vs public + version base (e.g. "internal/11.0.1xx" or "11.0.1xx")
            var groupKey = isInternal ? $"internal/{versionBase}" : versionBase;

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

    private static async Task<IList<MajorReleaseIndexItem>?> FetchReleasesIndexAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var indexData = await http.GetFromJsonAsync<MajorReleasesIndex>(
                "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json",
                KebabCaseOptions);
            return indexData?.ReleasesIndex;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches the per-channel releases.json for each supported channel and extracts
    /// all SDK versions from the latest release. Returns a map of channel → SDK version list.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> FetchAllChannelSdksAsync(
        IList<MajorReleaseIndexItem>? entries, HashSet<string> supportedChannels)
    {
        var result = new Dictionary<string, List<string>>();
        if (entries is null)
        {
            return result;
        }

        var tasks = new List<(string Channel, Task<List<string>?> Task)>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ChannelVersion) ||
                string.IsNullOrEmpty(entry.ReleasesJson) ||
                !supportedChannels.Contains(entry.ChannelVersion))
            {
                continue;
            }

            var channel = entry.ChannelVersion;
            var url = entry.ReleasesJson;
            var task = _channelSdksCache.GetOrAdd(channel,
                _ => new Lazy<Task<List<string>?>>(() => FetchLatestReleaseSdksAsync(url))).Value;
            tasks.Add((channel, task));
        }

        await Task.WhenAll(tasks.Select(t => t.Task));

        foreach (var (channel, task) in tasks)
        {
            var sdks = await task;
            if (sdks is not null)
            {
                result[channel] = sdks;
            }
        }

        return result;
    }

    private static async Task<List<string>?> FetchLatestReleaseSdksAsync(string releasesJsonUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var data = await http.GetFromJsonAsync<ChannelReleasesFile>(releasesJsonUrl, KebabCaseOptions);
            var latestRelease = data?.Releases?.FirstOrDefault();
            return latestRelease?.Sdks?
                .Select(s => s.Version)
                .Where(v => !string.IsNullOrEmpty(v))
                .Cast<string>()
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions KebabCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
    };

    // Minimal models for per-channel releases.json — we need the sdks array
    // which isn't available in Dotnet.Release.Releases 1.0.0's PatchRelease type.
    private sealed record ChannelReleasesFile(IList<ChannelRelease>? Releases);
    private sealed record ChannelRelease(IList<ChannelSdkEntry>? Sdks);
    private sealed record ChannelSdkEntry(string? Version);

    /// <summary>
    /// Builds a set of (versionBase, suffix) pairs representing branches that match
    /// released SDK versions. Uses per-channel SDK lists from releases.json when available,
    /// falling back to the index's latest-sdk field.
    /// </summary>
    internal static HashSet<(string VersionBase, string? Suffix)> GetReleasedBranches(
        IList<MajorReleaseIndexItem>? entries, HashSet<string> supportedChannels,
        Dictionary<string, List<string>>? channelSdks = null)
    {
        var released = new HashSet<(string VersionBase, string? Suffix)>();
        if (entries is null)
        {
            return released;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.ChannelVersion))
            {
                continue;
            }

            if (!supportedChannels.Contains(entry.ChannelVersion))
            {
                continue;
            }

            // Use per-channel SDK list from releases.json when available
            var sdkVersions = channelSdks?.GetValueOrDefault(entry.ChannelVersion);
            if (sdkVersions is not null)
            {
                foreach (var sdk in sdkVersions)
                {
                    AddParsedSdk(released, sdk, entry.ChannelVersion);
                }
            }
            else if (!string.IsNullOrEmpty(entry.LatestSdk))
            {
                // Fall back to latest-sdk from the index
                AddParsedSdk(released, entry.LatestSdk, entry.ChannelVersion);
            }
        }

        return released;

        static void AddParsedSdk(HashSet<(string, string?)> set, string sdk, string expectedChannel)
        {
            var parsed = ParseSdkVersion(sdk);
            if (parsed is not null && parsed.Value.Channel == expectedChannel)
            {
                set.Add((parsed.Value.VersionBase, parsed.Value.Suffix));
            }
        }
    }

    /// <summary>
    /// Parses an SDK version string into its channel, version base, and branch suffix.
    /// For prerelease SDKs (e.g. "11.0.100-preview.3.26207.106") the version base is
    /// the band ("11.0.1xx") since preview branches use band names.
    /// For GA SDKs (e.g. "10.0.303") the version base is the specific version ("10.0.303")
    /// since GA release branches use specific version numbers.
    /// </summary>
    internal static (string Channel, string VersionBase, string? Suffix)? ParseSdkVersion(string sdkVersion)
    {
        var match = SdkVersionPattern.Match(sdkVersion);
        if (!match.Success)
        {
            return null;
        }

        var major = match.Groups[1].Value;
        var minor = match.Groups[2].Value;
        var patch = int.Parse(match.Groups[3].Value);
        var channel = $"{major}.{minor}";

        string? suffix = null;
        if (match.Groups[4].Success && match.Groups[5].Success)
        {
            var type = match.Groups[4].Value.ToLowerInvariant();
            var number = match.Groups[5].Value;
            suffix = $"-{type}{number}";
        }

        // Prerelease branches use band names (e.g. release/11.0.1xx-preview3)
        // GA release branches use specific versions (e.g. release/10.0.303)
        var band = patch / 100;
        var versionBase = suffix is not null
            ? $"{major}.{minor}.{band}xx"
            : $"{major}.{minor}.{patch}";

        return (channel, versionBase, suffix);
    }

    internal static HashSet<string>? GetSupportedChannels(
        IList<MajorReleaseIndexItem>? entries, IReadOnlyCollection<SupportPhase>? supportPhases, int? minVersion)
    {
        if (entries is null)
        {
            return null;
        }

        var phases = supportPhases is { Count: > 0 }
            ? supportPhases.ToHashSet()
            : DefaultPhases;

        return entries
            .Where(e => !string.IsNullOrEmpty(e.ChannelVersion))
            .Where(e => phases.Contains(e.SupportPhase))
            .Where(e =>
            {
                var parts = e.ChannelVersion!.Split('.');
                return parts.Length >= 2 && int.TryParse(parts[0], out var major)
                    && (minVersion is null || major >= minVersion);
            })
            .Select(e => e.ChannelVersion!)
            .ToHashSet();
    }
}
