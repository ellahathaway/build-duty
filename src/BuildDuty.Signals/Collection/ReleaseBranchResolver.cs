using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BuildDuty.Signals.Configuration;
using Dotnet.Release;
using Dotnet.Release.Releases;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Signals.Collection;

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
        new(@"^refs/heads/((internal/)?release/((\d+)\.(\d+)\.(\d+)xx)(-.+)?)$", RegexOptions.Compiled);

    private static readonly Regex SuffixPattern =
        new(@"^-(preview|rc)(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SdkVersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)(?:-(preview|rc)\.(\d+))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _pipelineRepoCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>?>>> _repoBranchCache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<List<string>>>> _resolvedCache = new();

    private record PipelineInfo(string Org, string Project, int PipelineId);

    private readonly Lazy<Task<IList<MajorReleaseIndexItem>?>> _releasesIndex;

    public ReleaseBranchResolver()
    {
        _releasesIndex = new Lazy<Task<List<IndexEntry>?>>(FetchReleasesIndexAsync);
    }

    /// <summary>
    /// Resolves branches for a pipeline using the <see cref="ReleaseBranchConfig"/>.
    /// Results are cached per pipeline config.
    /// </summary>
    public Task<List<string>> ResolveAsync(
        VssConnection connection, string project, int pipelineId,
        ReleaseBranchConfig releaseConfig)
    {
        var org = connection.Uri.GetLeftPart(UriPartial.Authority);
        var phases = releaseConfig.SupportPhases is { Count: > 0 }
            ? string.Join(",", releaseConfig.SupportPhases.OrderBy(p => p))
            : "";
        var cacheKey = $"{org}|{project}|{pipelineId}|{phases}|{releaseConfig.MinVersion}";
        return _resolvedCache.GetOrAdd(cacheKey,
            _ => new Lazy<Task<List<string>>>(() =>
                ResolveInternalAsync(connection, project, pipelineId, releaseConfig.SupportPhases, releaseConfig.MinVersion))).Value;
    }

    private async Task<List<string>> ResolveInternalAsync(
        VssConnection connection, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        var org = connection.Uri.GetLeftPart(UriPartial.Authority);

        var pipelineKey = $"{org}|{project}|{pipelineId}";
        var repoName = await _pipelineRepoCache.GetOrAdd(pipelineKey,
            _ => new Lazy<Task<string?>>(() => FetchPipelineRepoAsync(connection, project, pipelineId))).Value;

        if (string.IsNullOrWhiteSpace(repoName))
        {
            return ["main"];
        }

        var repoKey = $"{org}|{project}|{repoName}";
        var rawBranches = await _repoBranchCache.GetOrAdd(repoKey,
            _ => new Lazy<Task<List<string>?>>(() => FetchRepoBranchesAsync(connection, project, repoName))).Value;

        if (rawBranches is null)
        {
            return ["main"];
        }

        var index = await _releasesIndex.Value;
        var supportedChannels = GetSupportedChannels(index, supportPhases, minVersion);
        if (supportedChannels is null)
        {
            return ["main"];
        }

        var channelSdks = await FetchAllChannelSdksAsync(index, supportedChannels);
        var releasedBranches = GetReleasedBranches(index, supportedChannels, channelSdks);
        return FilterBranches(rawBranches, supportedChannels, releasedBranches);
    }

    /// <summary>
    /// Filters raw branch refs to supported channels, keeping only the latest suffix
    /// per SDK band. Excludes branches that match a just-released SDK version.
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

            var branchName = match.Groups[1].Value;
            var isInternal = match.Groups[2].Success;
            var versionBase = match.Groups[3].Value;
            var channel = $"{match.Groups[4].Value}.{match.Groups[5].Value}";
            var isBand = match.Groups[7].Success;
            var suffix = match.Groups[8].Success ? match.Groups[8].Value : null;

            if (!supportedChannels.Contains(channel))
            {
                continue;
            }

            if (releasedBranches?.Contains((versionBase, suffix)) == true)
            {
                continue;
            }

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
    /// Compares two branch suffixes for recency.
    /// Order: (no suffix/GA) > rc{N} > go-live{N} > preview{N}. Higher N wins within a type.
    /// </summary>
    internal static int CompareSuffix(string? a, string? b)
    {
        return GetSuffixSortKey(a).CompareTo(GetSuffixSortKey(b));

        static (int TypeOrder, int Number) GetSuffixSortKey(string? suffix)
        {
            if (suffix is null)
            {
                return (3, 0);
            }

            var match = SuffixPattern.Match(suffix);
            if (!match.Success)
            {
                return (-1, 0);
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

    private sealed record ChannelReleasesFile(IList<ChannelRelease>? Releases);
    private sealed record ChannelRelease(IList<ChannelSdkEntry>? Sdks);
    private sealed record ChannelSdkEntry(string? Version);

    /// <summary>
    /// Builds a set of (versionBase, suffix) pairs representing branches that match
    /// released SDK versions.
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
