using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using Maestro.Common;
using Microsoft.Deployment.DotNet.Releases;
using Microsoft.Extensions.Logging;

namespace BuildDuty.Signals.Collection;

public sealed class ReleaseBranchResolver
{
    private static readonly Regex ReleaseBranchPatternXx =
        new(@"^refs/heads/((internal/)?release/((?<major>\d+)\.(?<minor>\d+)\.(?<featureBand>\d)xx)(?:-(?<preReleaseLabel>preview|rc)\.?(?<preReleaseVersion>\d+))?)$", RegexOptions.Compiled);
    private static readonly Regex ReleaseBranchPatternNnn =
        new(@"^refs/heads/((internal/)?release/((?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d{3}))(?:-(?<preReleaseLabel>preview|rc)\.?(?<preReleaseVersion>\d+))?)$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<preReleaseLabel>preview|rc)\.(?<preReleaseVersion>\d))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IRemoteTokenProvider _tokenProvider;
    private readonly ILogger _logger;
    private readonly Lazy<Task<Dictionary<string, Product>>> _productsTask;
    private readonly ConcurrentDictionary<string, Lazy<Task<ReadOnlyCollection<ProductRelease>>>> _releasesCache = new();
    private readonly ConcurrentDictionary<PipelineInfo, List<string>> _resolvedCache = new();
    private record PipelineInfo(string Org, string Project, int PipelineId);

    public ReleaseBranchResolver(IRemoteTokenProvider tokenProvider, ILogger logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
        _productsTask = new Lazy<Task<Dictionary<string, Product>>>(LoadProductsAsync);
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

        var products = await _productsTask.Value;
        if (!products.TryGetValue(branchChannel, out var product))
        {
            // Unknown channel: only allow explicit pre-release branches when Preview is requested.
            return match.Groups["preReleaseLabel"].Success && releaseConfig.SupportPhases.Contains(SupportPhase.Preview);
        }

        if (!releaseConfig.SupportPhases.Contains(product.SupportPhase))
        {
            return false;
        }

        // Pre-release branches (preview/rc) are only valid for Preview or GoLive channels.
        if (match.Groups["preReleaseLabel"].Success &&
            product.SupportPhase != SupportPhase.Preview &&
            product.SupportPhase != SupportPhase.GoLive)
        {
            return false;
        }

        var branchPatch = match.Groups["patch"].Value;
        var branchFeatureBand = match.Groups["featureBand"].Success
            ? int.Parse(match.Groups["featureBand"].Value)
            : int.Parse(branchPatch[0].ToString());

        if (product.SupportPhase == SupportPhase.Preview || product.SupportPhase == SupportPhase.GoLive)
        {
            var latestReleaseMatch = ValidateAndParseVersion(product.LatestReleaseVersion.ToString(), branchChannel);

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

        // Fetch per-channel release details lazily and cache by channel.
        var releases = await _releasesCache.GetOrAdd(
            branchChannel,
            _ => new Lazy<Task<ReadOnlyCollection<ProductRelease>>>(() => product.GetReleasesAsync())).Value;

        if (product.SupportPhase == SupportPhase.EOL)
        {
            // For EOL releases, include up to latest known feature band (derived from released SDKs).
            var latestFeatureBand = GetLatestReleasedFeatureBand(releases);
            return branchFeatureBand <= latestFeatureBand;
        }

        // Active releases include all Nxx feature-band branches.
        // For NNN branches, exclude branches whose SDK version has already been released.
        if (!match.Groups["featureBand"].Success)
        {
            var releasedSdkVersions = GetReleasedSdkVersions(releases);
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

    private static async Task<Dictionary<string, Product>> LoadProductsAsync()
    {
        var products = await ProductCollection.GetAsync();
        return products.ToDictionary(p => p.ProductVersion);
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
    private static HashSet<string> GetReleasedSdkVersions(ReadOnlyCollection<ProductRelease> releases)
    {
        var versions = new HashSet<string>();
        foreach (var release in releases)
        {
            foreach (var sdk in release.Sdks)
            {
                var sdkMatch = VersionPattern.Match(sdk.Version.ToString());
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
    private static int GetLatestReleasedFeatureBand(ReadOnlyCollection<ProductRelease> releases)
    {
        int maxBand = 0;
        foreach (var release in releases)
        {
            foreach (var sdk in release.Sdks)
            {
                var sdkMatch = VersionPattern.Match(sdk.Version.ToString());
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
}
