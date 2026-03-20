using System.Text.Json;
using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Discovers .NET release branches from an Azure DevOps Git repository,
/// filtered to branches matching supported .NET channels from the
/// <c>dotnet/core</c> releases index.
/// </summary>
public sealed class ReleaseBranchResolver
{
    private static readonly string[] DefaultSupportPhases =
        ["active", "maintenance", "preview", "go-live", "rc"];

    private static readonly Regex BranchLabelRegex = new(
        @"^(\d+)\.(\d+)\.(\d+(?:xx)?)(?:-(preview|rc)(\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReleaseVersionPreviewRegex = new(
        @"^(\d+)\.(\d+)\.\d+-(preview|rc)\.(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string ReleasesIndexUrl =
        "https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json";

    /// <summary>
    /// Resolve release branch names for a pipeline.
    /// Returns the list of branch names (e.g. "main", "release/9.0.1xx", "internal/release/8.0.4xx").
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveAsync(
        GitHttpClient gitClient,
        string project,
        ReleaseBranchConfig config,
        CancellationToken ct = default)
    {
        var phases = config.SupportPhases is { Count: > 0 }
            ? config.SupportPhases.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToArray()
            : DefaultSupportPhases;

        // Fetch branches and supported channels in parallel
        var branchNamesTask = ListBranchNamesAsync(gitClient, project, config.Repository, ct);
        var channelsTask = DownloadSupportedChannelsAsync(ct);
        await Task.WhenAll(branchNamesTask, channelsTask);

        var branchNames = branchNamesTask.Result;
        var allChannels = channelsTask.Result;

        var supportedChannels = FilterChannels(allChannels, phases, config.MinVersion);
        var supportedVersions = supportedChannels
            .Select(c => c.ChannelVersion)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Download per-channel releases.json to discover released SDK versions and previews
        var releaseData = await DownloadReleasedSdkVersionsAsync(supportedChannels, ct);

        var labels = CollectReleaseBranchLabels(branchNames);
        var filtered = FilterLabels(labels.AllLabels, supportedVersions);
        var final = FilterByPreviewAndLatestSpecific(filtered, releaseData);
        var sorted = SortLabels(final);

        var results = new List<string>();

        if (labels.HasMain)
            results.Add("main");

        foreach (var label in sorted)
        {
            if (labels.PublicBranches.TryGetValue(label, out var pub))
                results.Add(pub);
            if (labels.InternalBranches.TryGetValue(label, out var intern))
                results.Add(intern);
        }

        return results;
    }

    private static async Task<List<string>> ListBranchNamesAsync(
        GitHttpClient gitClient,
        string project,
        string repository,
        CancellationToken ct)
    {
        var refs = await gitClient.GetRefsAsync(
            project: project,
            repositoryId: repository,
            filter: "heads/",
            cancellationToken: ct);

        return refs
            .Select(r => r.Name.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? r.Name["refs/heads/".Length..]
                : r.Name)
            .ToList();
    }

    private static async Task<List<SupportedChannel>> DownloadSupportedChannelsAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        await using var stream = await http.GetStreamAsync(ReleasesIndexUrl, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var channels = new List<SupportedChannel>();

        if (doc.RootElement.TryGetProperty("releases-index", out var index) &&
            index.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in index.EnumerateArray())
            {
                if (!release.TryGetProperty("support-phase", out var phaseEl) ||
                    !release.TryGetProperty("channel-version", out var versionEl))
                    continue;

                var phase = phaseEl.GetString();
                var version = versionEl.GetString();
                if (string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(version))
                    continue;

                var releasesUrl = release.TryGetProperty("releases.json", out var urlEl)
                    ? urlEl.GetString()
                    : null;

                var (major, minor) = ParseChannelVersion(version);
                channels.Add(new SupportedChannel(version, phase, major, minor, releasesUrl));
            }
        }

        return channels;
    }

    /// <summary>
    /// Downloads per-channel releases.json files and extracts released SDK
    /// versions and released preview/RC identifiers.
    /// </summary>
    private static async Task<ReleaseData> DownloadReleasedSdkVersionsAsync(
        List<SupportedChannel> channels,
        CancellationToken ct)
    {
        var sdkVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releasedPreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var http = new HttpClient();

        var tasks = channels
            .Where(c => !string.IsNullOrWhiteSpace(c.ReleasesJsonUrl))
            .Select(async c =>
            {
                try
                {
                    await using var stream = await http.GetStreamAsync(c.ReleasesJsonUrl!, ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("releases", out var releases) ||
                        releases.ValueKind != JsonValueKind.Array)
                        return (Sdks: Array.Empty<string>(), Previews: Array.Empty<string>());

                    var versions = new List<string>();
                    var previews = new List<string>();

                    foreach (var rel in releases.EnumerateArray())
                    {
                        // Extract release-version to detect released previews/RCs
                        if (rel.TryGetProperty("release-version", out var rvEl))
                        {
                            var rv = rvEl.GetString();
                            if (!string.IsNullOrWhiteSpace(rv))
                            {
                                var pm = ReleaseVersionPreviewRegex.Match(rv);
                                if (pm.Success)
                                {
                                    // Key like "11.0-preview-2"
                                    var key = $"{pm.Groups[1].Value}.{pm.Groups[2].Value}-{pm.Groups[3].Value.ToLowerInvariant()}-{pm.Groups[4].Value}";
                                    previews.Add(key);
                                }
                            }
                        }

                        // Try "sdks" array first, then "sdk" object
                        if (rel.TryGetProperty("sdks", out var sdksEl) && sdksEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sdk in sdksEl.EnumerateArray())
                            {
                                if (sdk.TryGetProperty("version", out var ver))
                                {
                                    var v = ver.GetString();
                                    if (!string.IsNullOrWhiteSpace(v))
                                        versions.Add(v);
                                }
                            }
                        }
                        else if (rel.TryGetProperty("sdk", out var sdkEl) && sdkEl.ValueKind == JsonValueKind.Object)
                        {
                            if (sdkEl.TryGetProperty("version", out var ver))
                            {
                                var v = ver.GetString();
                                if (!string.IsNullOrWhiteSpace(v))
                                    versions.Add(v);
                            }
                        }
                    }
                    return (Sdks: versions.ToArray(), Previews: previews.ToArray());
                }
                catch
                {
                    return (Sdks: Array.Empty<string>(), Previews: Array.Empty<string>());
                }
            });

        var results = await Task.WhenAll(tasks);
        foreach (var batch in results)
        {
            foreach (var v in batch.Sdks)
                sdkVersions.Add(v);
            foreach (var p in batch.Previews)
                releasedPreviews.Add(p);
        }

        return new ReleaseData(sdkVersions, releasedPreviews);
    }

    private static List<SupportedChannel> FilterChannels(
        List<SupportedChannel> channels,
        string[] phases,
        int? minVersion)
    {
        var phaseSet = new HashSet<string>(phases, StringComparer.OrdinalIgnoreCase);
        IEnumerable<SupportedChannel> filtered = channels.Where(c => phaseSet.Contains(c.SupportPhase));
        if (minVersion.HasValue)
            filtered = filtered.Where(c => c.Major >= minVersion.Value);

        return filtered
            .GroupBy(c => c.ChannelVersion, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    internal static BranchLabelCollection CollectReleaseBranchLabels(IEnumerable<string> branchNames)
    {
        var publicBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var internalBranches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool hasMain = false;

        foreach (var name in branchNames)
        {
            if (name.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                hasMain = true;
                continue;
            }

            var pubMatch = Regex.Match(name, @"^release/(\d+\.\d+\..+)$", RegexOptions.IgnoreCase);
            if (pubMatch.Success)
            {
                publicBranches[pubMatch.Groups[1].Value] = name;
                continue;
            }

            var intMatch = Regex.Match(name, @"^internal/release/(\d+\.\d+\..+)$", RegexOptions.IgnoreCase);
            if (intMatch.Success)
                internalBranches[intMatch.Groups[1].Value] = name;
        }

        var allLabels = publicBranches.Keys
            .Concat(internalBranches.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BranchLabelCollection(hasMain, publicBranches, internalBranches, allLabels);
    }

    internal static List<string> FilterLabels(IEnumerable<string> labels, HashSet<string> supportedChannels)
    {
        return labels
            .Select(l => (Label: l, Parsed: ParseBranchLabel(l)))
            .Where(x => x.Parsed is not null)
            .Where(x => supportedChannels.Contains(x.Parsed!.MajorMinor))
            .Select(x => x.Label)
            .ToList();
    }

    internal static List<string> FilterByPreviewAndLatestSpecific(
        IEnumerable<string> labels,
        ReleaseData? releaseData = null)
    {
        var grouped = new Dictionary<string, List<(string Label, BranchLabelInfo Parsed)>>();

        foreach (var label in labels)
        {
            var parsed = ParseBranchLabel(label);
            if (parsed is null) continue;

            var key = $"{parsed.MajorMinor}.{parsed.FeatureBand}";
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }
            list.Add((label, parsed));
        }

        var final = new List<string>();

        foreach (var group in grouped.Values)
        {
            // Keep all non-preview feature band versions
            final.AddRange(group
                .Where(x => x.Parsed.IsFeatureBand && !x.Parsed.IsPreview)
                .Select(x => x.Label));

            // Keep only latest preview/RC that hasn't been released yet
            var previewCandidates = group
                .Where(x => x.Parsed.IsPreview)
                .OrderByDescending(x => x.Parsed.Band)
                .ThenByDescending(x => x.Parsed.SuffixType.Equals("rc", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(x => x.Parsed.SuffixNumber)
                .ToList();

            foreach (var candidate in previewCandidates)
            {
                // Check if this preview/RC has been released
                var previewKey = $"{candidate.Parsed.MajorMinor}-{candidate.Parsed.SuffixType}-{candidate.Parsed.SuffixNumber}";
                if (releaseData?.ReleasedPreviews.Contains(previewKey) == true)
                    continue; // This preview shipped — skip it

                final.Add(candidate.Label);
                break; // Only keep the latest unreleased preview
            }

            // For specific versions: only keep if no higher version in the
            // same feature band has been released (i.e. it's still the latest
            // or hasn't shipped yet).
            var specificVersions = group
                .Where(x => !x.Parsed.IsFeatureBand && !x.Parsed.IsPreview)
                .ToList();

            if (specificVersions.Count > 0 && releaseData?.SdkVersions is { Count: > 0 })
            {
                foreach (var (label, parsed) in specificVersions)
                {
                    // Check if any higher SDK version in the same feature band
                    // has been released. If so, this branch is stale.
                    bool isSuperseded = releaseData.SdkVersions.Any(sdk =>
                    {
                        var sdkParsed = ParseBranchLabel(sdk);
                        return sdkParsed is not null
                            && sdkParsed.MajorMinor == parsed.MajorMinor
                            && sdkParsed.FeatureBand == parsed.FeatureBand
                            && !sdkParsed.IsFeatureBand
                            && !sdkParsed.IsPreview
                            && sdkParsed.Band > parsed.Band;
                    });

                    if (!isSuperseded)
                        final.Add(label);
                }
            }
            else
            {
                // No release data — fall back to keeping the latest specific
                var latestSpecific = specificVersions
                    .OrderByDescending(x => x.Parsed.Band)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(latestSpecific.Label))
                    final.Add(latestSpecific.Label);
            }
        }

        return final.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<string> SortLabels(IEnumerable<string> labels)
    {
        return labels
            .Select(l => (Label: l, Parsed: ParseBranchLabel(l)))
            .Where(x => x.Parsed is not null)
            .OrderByDescending(x => x.Parsed!.Major)
            .ThenByDescending(x => x.Parsed!.Minor)
            .ThenByDescending(x => x.Parsed!.FeatureBand)
            .ThenBy(x => x.Parsed!.IsFeatureBand ? 1 : 0)
            .ThenByDescending(x => x.Parsed!.Band)
            .ThenBy(x => x.Parsed!.SuffixType switch { "" => 0, "rc" => 1, _ => 2 })
            .ThenByDescending(x => x.Parsed!.SuffixNumber)
            .Select(x => x.Label)
            .ToList();
    }

    private static BranchLabelInfo? ParseBranchLabel(string label)
    {
        var match = BranchLabelRegex.Match(label);
        if (!match.Success) return null;

        int major = int.Parse(match.Groups[1].Value);
        int minor = int.Parse(match.Groups[2].Value);
        string bandString = match.Groups[3].Value;
        string suffixType = match.Groups[4].Success ? match.Groups[4].Value.ToLowerInvariant() : "";
        int suffixNumber = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 0;

        bool isFeatureBand = bandString.EndsWith("xx", StringComparison.OrdinalIgnoreCase);
        int band = isFeatureBand
            ? int.Parse(bandString.Replace("xx", "00", StringComparison.OrdinalIgnoreCase))
            : int.Parse(bandString);

        int featureBand = (int)Math.Floor(band / 100d) * 100;
        bool isPreview = suffixType is "preview" or "rc";

        return new BranchLabelInfo(major, minor, $"{major}.{minor}", band, bandString,
            isFeatureBand, suffixType, suffixNumber, isPreview, featureBand);
    }

    private static (int Major, int Minor) ParseChannelVersion(string channel)
    {
        var parts = channel.Split('.');
        if (parts.Length < 2) return (0, 0);
        int.TryParse(parts[0], out int major);
        int.TryParse(parts[1], out int minor);
        return (major, minor);
    }

    internal sealed record BranchLabelCollection(
        bool HasMain,
        Dictionary<string, string> PublicBranches,
        Dictionary<string, string> InternalBranches,
        List<string> AllLabels);

    internal sealed record BranchLabelInfo(
        int Major, int Minor, string MajorMinor, int Band, string BandString,
        bool IsFeatureBand, string SuffixType, int SuffixNumber,
        bool IsPreview, int FeatureBand);

    internal sealed record SupportedChannel(
        string ChannelVersion, string SupportPhase, int Major, int Minor, string? ReleasesJsonUrl);

    internal sealed record ReleaseData(
        HashSet<string> SdkVersions,
        HashSet<string> ReleasedPreviews);
}
