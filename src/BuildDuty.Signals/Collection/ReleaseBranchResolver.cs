using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using Dotnet.Release;
using Dotnet.Release.Releases;
using Maestro.Common;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Signals.Collection;

public sealed class ReleaseBranchResolver
{
    private static readonly string ReleasesIndexUrl = "https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json";
    private static readonly Regex ReleaseBranchPatternXx =
        new(@"^refs/heads/((internal/)?release/((?<major>\d+)\.(?<minor>\d+)\.(?<featureBand>\d)xx)(?:-(?<preReleaseLabel>preview|rc)\.?(?<preReleaseVersion>\d+))?)$", RegexOptions.Compiled);
    private static readonly Regex ReleaseBranchPatternNnn =
        new(@"^refs/heads/((internal/)?release/((?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d{3}))(?:-(?<preReleaseLabel>preview|rc)\.?(?<preReleaseVersion>\d+))?)$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<preReleaseLabel>preview|rc)\.(?<preReleaseVersion>\d))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions ReleaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    private readonly IRemoteTokenProvider _tokenProvider;
    private readonly ILogger _logger;
    private readonly Lazy<Task<Dictionary<string, MajorReleaseIndexItem>>> _releaseIndexEntriesTask;
    private readonly ConcurrentDictionary<string, Lazy<Task<MajorReleases>>> _releasesCache = new();
    private readonly ConcurrentDictionary<PipelineInfo, List<string>> _resolvedCache = new();
    private record PipelineInfo(string Org, string Project, int PipelineId);

    public ReleaseBranchResolver(IRemoteTokenProvider tokenProvider, ILogger logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
        _releaseIndexEntriesTask = new Lazy<Task<Dictionary<string, MajorReleaseIndexItem>>>(LoadReleaseIndexAsync);
    }

    /// <summary>
    /// Resolves branches for a pipeline using the <see cref="ReleaseBranchConfig"/>.
    /// Results are cached per pipeline config.
    /// </summary>
    public async Task<List<string>> ResolveAsync(
        string org,
        string project,
        int pipelineId,
        ReleaseBranchConfig releaseConfig)
    {
        var pipelineInfo = new PipelineInfo(org, project, pipelineId);
        if (!_resolvedCache.ContainsKey(pipelineInfo))
        {
            var branches = await ResolveBranchesAsync(pipelineInfo, releaseConfig);
            _resolvedCache[pipelineInfo] = branches;
            return branches;
        }
        return _resolvedCache[pipelineInfo];
    }

    private async Task<List<string>> ResolveBranchesAsync(PipelineInfo pipelineInfo, ReleaseBranchConfig releaseConfig)
    {
        // Resolve repository for the configured pipeline.
        using var buildClient = await _tokenProvider.GetAzureDevOpsBuildClient(pipelineInfo.Org);
        var definition = await buildClient.GetDefinitionAsync(pipelineInfo.Project, pipelineInfo.PipelineId);
        var repoName = definition.Repository.Name ?? throw new Exception($"Pipeline '{pipelineInfo.PipelineId}' repository not found");
        // Query public and internal release refs, plus main.
        using var gitClient = await _tokenProvider.GetAzureDevOpsGitClient(pipelineInfo.Org);
        var releaseTask = gitClient.GetRefsAsync(pipelineInfo.Project, repoName, filter: "heads/release/");
        var internalTask = gitClient.GetRefsAsync(pipelineInfo.Project, repoName, filter: "heads/internal/release/");
        var mainTask = gitClient.GetRefsAsync(pipelineInfo.Project, repoName, filter: "heads/main");
        var allRefs = await Task.WhenAll(releaseTask, internalTask, mainTask);
        var branches = allRefs.SelectMany(r => r).Select(r => r.Name);
        _logger.LogDebug("    Pipeline {PipelineId}: repo='{RepoName}', {Count} refs found", pipelineInfo.PipelineId, repoName, allRefs.Sum(r => r.Count));

        var resolvedBranchesTasks = branches.Select(async branch => await MatchesReleaseConfigFiltersAsync(branch, releaseConfig) ? branch : null)
            .Where(task => task is not null);
        var resolvedBranches = await Task.WhenAll(resolvedBranchesTasks);
        return resolvedBranches
            .Where(b => b is not null)
            .Cast<string>()
            .Select(StripRefsHeadsPrefix)
            .ToList();
    }

    private static string StripRefsHeadsPrefix(string branch) =>
        branch.StartsWith("refs/heads/") ? branch["refs/heads/".Length..] : branch;

    private async Task<bool> MatchesReleaseConfigFiltersAsync(string branch, ReleaseBranchConfig releaseConfig)
    {
        // Filtering summary:
        // - main is included only for Active support.
        // - release branches must match Nxx or NNN patterns and MinVersion.
        // - Preview/GoLive branches must be newer than latest release in channel.
        // - EOL includes up to latest feature band.
        // - Active includes all available feature bands.

        if (branch.Equals("refs/heads/main"))
        {
            // main represents next in-development release line.
            return releaseConfig.SupportPhases.Contains(SupportPhase.Active);
        }

        var match = ReleaseBranchPatternXx.Match(branch);
        if (!match.Success)
        {
            match = ReleaseBranchPatternNnn.Match(branch);
        }
        if (!match.Success)
        {
            return false;
        }
        var major = int.Parse(match.Groups["major"].Value);
        if (major < releaseConfig.MinVersion)
        {
            return false;
        }
        if (releaseConfig.MaxVersion > 0 && major > releaseConfig.MaxVersion)
        {
            return false;
        }

        // Map to a channel
        var minor = match.Groups["minor"].Value;
        string branchChannel = $"{major}.{minor}";

        var releaseIndexEntries = await _releaseIndexEntriesTask.Value;
        if (!releaseIndexEntries.TryGetValue(branchChannel, out var indexEntry))
        {
            // Unknown channel: only allow explicit pre-release branches when Preview is requested.
            return match.Groups["preReleaseLabel"].Success && releaseConfig.SupportPhases.Contains(SupportPhase.Preview);
        }

        // Fetch channel metadata lazily and cache by channel.
        var majorRelease = await _releasesCache.GetOrAdd(
            branchChannel,
            _ => new Lazy<Task<MajorReleases>>(() => LoadMajorReleaseAsync(indexEntry, branchChannel))).Value;

        if (!releaseConfig.SupportPhases.Contains(majorRelease.SupportPhase))
        {
            return false;
        }

        // Pre-release branches (preview/rc) are only valid for Preview or GoLive channels.
        if (match.Groups["preReleaseLabel"].Success &&
            majorRelease.SupportPhase != SupportPhase.Preview &&
            majorRelease.SupportPhase != SupportPhase.GoLive)
        {
            return false;
        }

        var branchPatch = match.Groups["patch"].Value;
        var branchFeatureBand = match.Groups["featureBand"].Success 
            ? int.Parse(match.Groups["featureBand"].Value)
            : int.Parse(branchPatch[0].ToString());

        if (majorRelease.SupportPhase == SupportPhase.Preview || majorRelease.SupportPhase == SupportPhase.GoLive)
        {
            var latestReleaseMatch = ValidateAndParseVersion(majorRelease.LatestRelease, branchChannel);

            var preReleaseLabel = match.Groups["preReleaseLabel"].Value;
            var latestPreReleaseLabel = latestReleaseMatch.Groups["preReleaseLabel"].Value;
            if (!preReleaseLabel.Equals(latestPreReleaseLabel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var preReleaseVersion = int.Parse(match.Groups["preReleaseVersion"].Value);
            var latestPreReleaseVersion = int.Parse(latestReleaseMatch.Groups["preReleaseVersion"].Value);
            if (preReleaseVersion <= latestPreReleaseVersion)
            {
                return false;
            }
            return true;
        }

        if (majorRelease.SupportPhase == SupportPhase.Eol)
        {
            // For EOL releases, include up to latest known feature band (derived from released SDKs).
            var latestFeatureBand = GetLatestReleasedFeatureBand(majorRelease);
            return branchFeatureBand <= latestFeatureBand;
        }

        // Active releases include all Nxx feature-band branches.
        // For NNN branches, exclude branches whose SDK version has already been released.
        if (!match.Groups["featureBand"].Success)
        {
            var releasedSdkVersions = GetReleasedSdkVersions(majorRelease);
            // Branch patch is the full SDK patch (e.g., 107, 203, 300).
            // The SDK version for this branch is "{major}.{minor}.{patch}".
            var branchSdkVersion = $"{major}.{minor}.{branchPatch}";
            if (releasedSdkVersions.Contains(branchSdkVersion))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Dictionary<string, MajorReleaseIndexItem>> LoadReleaseIndexAsync()
    {
        using var client = CreatePublicHttpClient();
        var majorReleaseIndex = await client.GetFromJsonAsync<MajorReleasesIndex>(ReleasesIndexUrl, ReleaseJsonOptions)
            ?? throw new Exception("Failed to fetch releases index");
        return majorReleaseIndex.ReleasesIndex.ToDictionary(r => r.ChannelVersion);
    }

    private async Task<MajorReleases> LoadMajorReleaseAsync(MajorReleaseIndexItem indexEntry, string branchChannel)
    {
        using var client = CreatePublicHttpClient();
        return await client.GetFromJsonAsync<MajorReleases>(indexEntry.ReleasesJson, ReleaseJsonOptions)
            ?? throw new Exception($"Failed to fetch major release data for channel '{branchChannel}'");
    }

    private Match ValidateAndParseVersion(string version, string branchChannel)
    {
        var match = VersionPattern.Match(version);
        if (!match.Success)
        {
            throw new Exception($"Invalid version format for latest release '{version}' in channel '{branchChannel}'");
        }
        return match;
    }

    /// <summary>
    /// Extracts all released SDK base versions (e.g., "10.0.107", "10.0.203") from the release data.
    /// Only includes stable (non-prerelease) SDK versions.
    /// </summary>
    private static HashSet<string> GetReleasedSdkVersions(MajorReleases majorRelease)
    {
        var versions = new HashSet<string>();
        foreach (var release in majorRelease.Releases)
        {
            if (release.Sdks is null)
            {
                continue;
            }

            foreach (var sdk in release.Sdks)
            {
                // Parse just the base version (major.minor.patch) from full SDK version strings
                // e.g., "10.0.107" from "10.0.107" or "10.0.100-rc.1.25451.107" → skip pre-release
                var sdkMatch = VersionPattern.Match(sdk.Version);
                if (sdkMatch.Success && !sdkMatch.Groups["preReleaseLabel"].Success)
                {
                    var major = sdkMatch.Groups["major"].Value;
                    var minor = sdkMatch.Groups["minor"].Value;
                    var patch = sdkMatch.Groups["patch"].Value;
                    versions.Add($"{major}.{minor}.{patch}");
                }
            }
        }
        return versions;
    }

    /// <summary>
    /// Gets the highest feature band number from released stable SDKs.
    /// Feature band is the first digit of the SDK patch (e.g., 3 for "9.0.313").
    /// </summary>
    private static int GetLatestReleasedFeatureBand(MajorReleases majorRelease)
    {
        int maxBand = 0;
        foreach (var release in majorRelease.Releases)
        {
            if (release.Sdks is null)
            {
                continue;
            }

            foreach (var sdk in release.Sdks)
            {
                var sdkMatch = VersionPattern.Match(sdk.Version);
                if (sdkMatch.Success && !sdkMatch.Groups["preReleaseLabel"].Success)
                {
                    var patch = sdkMatch.Groups["patch"].Value;
                    var band = int.Parse(patch[0].ToString());
                    if (band > maxBand)
                    {
                        maxBand = band;
                    }
                }
            }
        }
        return maxBand;
    }

    private static HttpClient CreatePublicHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BuildDuty/1.0");
        return client;
    }

    private sealed class MajorReleases
    {
        [JsonPropertyName("channel-version")]
        public required string ChannelVersion { get; set; }

        [JsonPropertyName("support-phase")]
        public SupportPhase SupportPhase { get; set; }

        [JsonPropertyName("latest-release")]
        public required string LatestRelease { get; set; }

        [JsonPropertyName("releases")]
        public required List<Release> Releases { get; set; }
    }

    private sealed class Release
    {
        [JsonPropertyName("sdks")]
        public List<Sdk>? Sdks { get; set; }
    }

    private sealed class Sdk
    {
        [JsonPropertyName("version")]
        public required string Version { get; set; }

        [JsonPropertyName("runtime-version")]
        public required string RuntimeVersion { get; set; }
    }
}
