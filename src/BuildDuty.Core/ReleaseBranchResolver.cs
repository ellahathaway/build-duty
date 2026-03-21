using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BuildDuty.Core;

/// <summary>
/// Resolves active branches for an ADO pipeline by:
/// 1. Looking up the pipeline's repository via az CLI
/// 2. Listing branches in that repo via az CLI
/// 3. Filtering to branches matching active .NET release channels
///
/// Caches pipeline→repo mappings, branch listings, and the releases index
/// for the lifetime of the process to avoid redundant CLI/HTTP calls.
/// </summary>
internal static class ReleaseBranchResolver
{
    private const string ReleasesIndexUrl =
        "https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json";

    private static readonly HashSet<string> DefaultPhases =
        ["active", "maintenance", "preview", "go-live", "rc"];

    // Matches release/{M}.{m}.{F}xx and internal/release/{M}.{m}.{F}xx (no suffix)
    private static readonly Regex ReleaseBranchPattern =
        new(@"^refs/heads/((internal/)?release/(\d+)\.(\d+)\.\d+xx)$", RegexOptions.Compiled);

    // Caches: pipeline ID → repo name, repo key → branch list, releases index
    private static readonly ConcurrentDictionary<string, string> PipelineRepoCache = new();
    private static readonly ConcurrentDictionary<string, List<string>> RepoBranchCache = new();
    private static List<IndexEntry>? s_releasesIndexCache;

    public static async Task<string> ResolveAsync(
        string org, string project, int pipelineId,
        string? supportPhases, int? minVersion)
    {
        // Normalize org to full URL if just the org name was provided
        if (!org.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            org = $"https://dev.azure.com/{org}";

        // Step 1: Get the pipeline's repository name (cached)
        var pipelineKey = $"{org}|{project}|{pipelineId}";
        if (!PipelineRepoCache.TryGetValue(pipelineKey, out var repoName))
        {
            var output = await RunAzAsync(
                $"pipelines show --id {pipelineId} --org {org} --project {project} " +
                "--query repository.name -o tsv");

            if (string.IsNullOrWhiteSpace(output))
                return JsonSerializer.Serialize(new { error = "Failed to discover repository for pipeline" });

            repoName = output.Trim();
            PipelineRepoCache[pipelineKey] = repoName;
        }

        // Step 2: List branches matching release/ and internal/release/ prefixes (cached per repo)
        var repoKey = $"{org}|{project}|{repoName}";
        if (!RepoBranchCache.TryGetValue(repoKey, out var rawBranches))
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
                return JsonSerializer.Serialize(new { error = "Failed to list branches" });

            rawBranches = new List<string>();
            if (releaseOutput is not null)
                rawBranches.AddRange(releaseOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
            if (internalOutput is not null)
                rawBranches.AddRange(internalOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

            RepoBranchCache[repoKey] = rawBranches;
        }

        // Step 3: Get supported channel versions from releases index (cached)
        var supportedChannels = await GetSupportedChannelsAsync(supportPhases, minVersion);
        if (supportedChannels is null)
            return JsonSerializer.Serialize(new { error = "Failed to fetch releases index" });

        // Step 4: Filter branches to those matching supported channels
        var branches = new List<string> { "main" };

        foreach (var line in rawBranches)
        {
            var match = ReleaseBranchPattern.Match(line);
            if (!match.Success) continue;

            var branchName = match.Groups[1].Value; // e.g. release/10.0.1xx or internal/release/10.0.1xx
            var channel = $"{match.Groups[3].Value}.{match.Groups[4].Value}"; // e.g. 10.0

            if (supportedChannels.Contains(channel))
                branches.Add(branchName);
        }

        return JsonSerializer.Serialize(branches);
    }

    private static async Task<HashSet<string>?> GetSupportedChannelsAsync(
        string? supportPhases, int? minVersion)
    {
        var phases = DefaultPhases;
        if (!string.IsNullOrWhiteSpace(supportPhases))
            phases = supportPhases.Split(',').Select(p => p.Trim().ToLowerInvariant()).ToHashSet();

        var entries = s_releasesIndexCache;
        if (entries is null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var indexData = await http.GetFromJsonAsync<ReleasesIndex>(ReleasesIndexUrl);
            if (indexData?.Entries is null)
                return null;

            entries = indexData.Entries;
            s_releasesIndexCache = entries;
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

    private sealed class IndexEntry
    {
        [JsonPropertyName("channel-version")]
        public string? ChannelVersion { get; set; }

        [JsonPropertyName("support-phase")]
        public string? SupportPhase { get; set; }
    }
}
