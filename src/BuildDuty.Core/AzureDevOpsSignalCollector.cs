using System.Diagnostics;
using System.Text.Json;
using BuildDuty.Core.Models;

namespace BuildDuty.Core;

/// <summary>
/// Deterministic signal collector for Azure DevOps pipelines.
/// Uses <c>az pipelines</c> CLI to query builds — no AI involved.
/// </summary>
public sealed class AzureDevOpsSignalCollector
{
    private readonly AzureDevOpsConfig _config;

    public AzureDevOpsSignalCollector(AzureDevOpsConfig config)
    {
        _config = config;
    }

    public async Task<CollectionResult> CollectAsync(WorkItemStore? store = null, CancellationToken ct = default)
    {
        var started = Stopwatch.StartNew();
        var signals = new List<CollectedSignal>();

        try
        {
            foreach (var org in _config.Organizations)
            {
                var orgUrl = NormalizeOrg(org.Url);

                foreach (var project in org.Projects)
                {
                    foreach (var pipeline in project.Pipelines)
                    {
                        ct.ThrowIfCancellationRequested();

                        var branches = await ResolveBranchesAsync(orgUrl, project.Name, pipeline, ct);
                        var builds = await GetLatestBuildsAsync(orgUrl, project.Name, pipeline.Id, branches, ct);
                        var statusFilter = pipeline.EffectiveStatus.Select(s => s.ToLowerInvariant()).ToHashSet();
                        var maxAge = pipeline.ParsedAge;
                        var cutoff = maxAge.HasValue ? DateTimeOffset.UtcNow - maxAge.Value : (DateTimeOffset?)null;

                        foreach (var build in builds)
                        {
                            // Skip builds older than the configured age
                            if (cutoff.HasValue && build.FinishTimeUtc.HasValue && build.FinishTimeUtc < cutoff)
                                continue;
                            var signal = ToSignal(orgUrl, project.Name, pipeline, build, statusFilter);

                            if (statusFilter.Contains(build.Result))
                            {
                                // Failure — include for AI to create work items
                                signals.Add(signal);
                            }
                            else if (store is not null)
                            {
                                // Success — only include if there's an unresolved work item
                                // the AI may want to resolve
                                var existingItems = await store.ListAsync();
                                if (existingItems.Any(i =>
                                    i.CorrelationId == signal.CorrelationId &&
                                    i.State != WorkItemState.Resolved))
                                {
                                    signals.Add(signal);
                                }
                            }
                        }
                    }
                }
            }

            return new CollectionResult
            {
                Source = "AzureDevOps",
                Success = true,
                Signals = signals,
                Duration = started.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new CollectionResult
            {
                Source = "AzureDevOps",
                Success = false,
                Error = ex.Message,
                Signals = signals,
                Duration = started.Elapsed,
            };
        }
    }

    private static async Task<List<string>> ResolveBranchesAsync(
        string orgUrl, string project, AzureDevOpsPipelineConfig pipeline, CancellationToken ct)
    {
        if (pipeline.Branches is { Count: > 0 })
            return pipeline.Branches;

        if (pipeline.Release is not null)
        {
            var phases = pipeline.Release.SupportPhases is { Count: > 0 }
                ? string.Join(",", pipeline.Release.SupportPhases)
                : null;

            var resolved = await ReleaseBranchResolver.ResolveAsync(
                orgUrl, project, pipeline.Id, phases, pipeline.Release.MinVersion);

            // Parse the JSON list result
            try
            {
                return JsonSerializer.Deserialize<List<string>>(resolved) ?? ["main"];
            }
            catch
            {
                return ["main"];
            }
        }

        return ["main"];
    }

    private static async Task<List<BuildInfo>> GetLatestBuildsAsync(
        string orgUrl, string project, int pipelineId, List<string> branches, CancellationToken ct)
    {
        var tasks = branches.Select(async branch =>
        {
            var output = await RunAzAsync(
                $"pipelines runs list --pipeline-ids {pipelineId} " +
                $"--branch {branch} --status completed --top 1 " +
                $"--query-order FinishTimeDesc " +
                $"--org {orgUrl} --project {project} -o json");

            if (output is null) return null;

            try
            {
                var builds = JsonSerializer.Deserialize<List<JsonElement>>(output);
                if (builds is null || builds.Count == 0) return null;

                var b = builds[0];
                var finishStr = b.TryGetProperty("finishTime", out var ft) ? ft.GetString() : null;
                return new BuildInfo
                {
                    Id = b.GetProperty("id").GetInt32(),
                    BuildNumber = b.TryGetProperty("buildNumber", out var bn) ? bn.GetString() ?? "" : "",
                    Result = ParseResult(b),
                    SourceBranch = branch,
                    FinishTime = finishStr,
                    FinishTimeUtc = DateTimeOffset.TryParse(finishStr, out var dto) ? dto : null,
                };
            }
            catch
            {
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(b => b is not null).ToList()!;
    }

    private static string ParseResult(JsonElement build)
    {
        if (!build.TryGetProperty("result", out var resultProp))
            return "unknown";

        // az CLI returns string results (e.g. "succeeded", "failed")
        if (resultProp.ValueKind == JsonValueKind.String)
            return resultProp.GetString()?.ToLowerInvariant() ?? "unknown";

        // MCP/REST returns numeric enums
        if (resultProp.ValueKind == JsonValueKind.Number)
        {
            return resultProp.GetInt32() switch
            {
                2 => "succeeded",
                4 => "partiallySucceeded",
                8 => "failed",
                32 => "canceled",
                _ => "unknown",
            };
        }

        return "unknown";
    }

    private static CollectedSignal ToSignal(
        string orgUrl, string project, AzureDevOpsPipelineConfig pipeline, BuildInfo build,
        HashSet<string> statusFilter)
    {
        var shortBranch = build.SourceBranch;
        var sanitized = shortBranch.Replace('/', '_').Replace('\\', '_');

        return new CollectedSignal
        {
            Id = $"wi_ado_{build.Id}",
            Title = $"[{pipeline.Name}] {shortBranch} — Build #{build.BuildNumber} {build.Result}",
            CorrelationId = $"corr_ado_{pipeline.Id}_{sanitized}",
            SignalType = "ado-pipeline-run",
            SignalRef = $"{orgUrl.TrimEnd('/')}/{project}/_build/results?buildId={build.Id}",
            Status = build.Result,
            Metadata = new()
            {
                ["pipelineId"] = pipeline.Id.ToString(),
                ["pipelineName"] = pipeline.Name,
                ["branch"] = shortBranch,
                ["buildNumber"] = build.BuildNumber,
                ["matchesFilter"] = statusFilter.Contains(build.Result) ? "true" : "false",
            },
        };
    }

    private static string NormalizeOrg(string org) =>
        org.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? org
            : $"https://dev.azure.com/{org}";

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

    private sealed class BuildInfo
    {
        public int Id { get; init; }
        public string BuildNumber { get; init; } = "";
        public string Result { get; init; } = "unknown";
        public string SourceBranch { get; init; } = "";
        public string? FinishTime { get; init; }
        public DateTimeOffset? FinishTimeUtc { get; init; }
    }
}
