using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDuty.Core;

/// <summary>
/// Deterministic client for fetching Azure DevOps build details.
/// Used by AI tools to avoid slow reference-doc + bash round-trips.
/// </summary>
public static class AzureDevOpsBuildClient
{
    /// <summary>
    /// Parses an ADO build URL into its components.
    /// Supports both <c>dev.azure.com/{org}</c> and <c>{org}.visualstudio.com</c> formats.
    /// </summary>
    public static (string OrgUrl, string Project, int BuildId)? ParseBuildUrl(string url)
    {
        // https://dev.azure.com/{org}/{project}/_build/results?buildId={id}
        var m = Regex.Match(url, @"https://dev\.azure\.com/([^/]+)/([^/]+)/_build/results\?buildId=(\d+)");
        if (m.Success)
            return ($"https://dev.azure.com/{m.Groups[1].Value}", m.Groups[2].Value, int.Parse(m.Groups[3].Value));

        // https://{org}.visualstudio.com/{project}/_build/results?buildId={id}
        m = Regex.Match(url, @"https://([^.]+)\.visualstudio\.com/([^/]+)/_build/results\?buildId=(\d+)");
        if (m.Success)
            return ($"https://dev.azure.com/{m.Groups[1].Value}", m.Groups[2].Value, int.Parse(m.Groups[3].Value));

        return null;
    }

    /// <summary>
    /// Returns failed tasks with their stage/job hierarchy and log IDs.
    /// Optionally filters to specific stages/jobs matching the given patterns.
    /// </summary>
    public static async Task<PipelineFailureInfo> GetFailedTasksAsync(
        string orgUrl, string project, int buildId, string? stageFilters = null)
    {
        // Fetch full timeline
        var timelineJson = await RunAzAsync(
            $"devops invoke --area build --resource timeline " +
            $"--route-parameters project={project} buildId={buildId} " +
            $"--org {orgUrl} -o json");

        if (timelineJson is null)
            return new PipelineFailureInfo { BuildId = buildId, Error = "Failed to fetch build timeline." };

        try
        {
            using var doc = JsonDocument.Parse(timelineJson);
            var records = doc.RootElement.GetProperty("records");

            // Index all records by ID
            var byId = new Dictionary<string, JsonElement>();
            foreach (var rec in records.EnumerateArray())
            {
                var id = rec.GetProperty("id").GetString();
                if (id is not null)
                    byId[id] = rec;
            }

            // Parse stage filter patterns
            var filters = ParseStageFilters(stageFilters);

            var failedTasks = new List<FailedTask>();
            foreach (var rec in records.EnumerateArray())
            {
                var type = rec.GetProperty("type").GetString();
                var result = rec.TryGetProperty("result", out var r) ? r.GetString() : null;

                // Include failed, canceled (timeout), and succeededWithIssues tasks
                if (type != "Task" || (result != "failed" && result != "canceled" && result != "succeededWithIssues"))
                    continue;

                var taskName = rec.GetProperty("name").GetString() ?? "(unknown)";
                var logId = rec.TryGetProperty("log", out var log) && log.TryGetProperty("id", out var lid)
                    ? lid.GetInt32() : (int?)null;

                // Walk parent chain to find job and stage names.
                // ADO timeline hierarchy can be: Task → Job → Stage
                // or: Task → Job → Phase → Stage (when phases exist)
                var parentId = rec.TryGetProperty("parentId", out var pid) ? pid.GetString() : null;
                string? jobName = null, stageName = null;

                if (parentId is not null && byId.TryGetValue(parentId, out var jobRec))
                {
                    jobName = jobRec.GetProperty("name").GetString();
                    // Walk up from the job record to find the Stage
                    var current = jobRec;
                    while (true)
                    {
                        var curParentId = current.TryGetProperty("parentId", out var cpid) ? cpid.GetString() : null;
                        if (curParentId is null || !byId.TryGetValue(curParentId, out var parentRec))
                            break;
                        var parentType = parentRec.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        if (parentType == "Stage")
                        {
                            stageName = parentRec.GetProperty("name").GetString();
                            break;
                        }
                        current = parentRec;
                    }
                }

                // Apply stage/job filters
                if (filters.Count > 0 && !MatchesFilters(stageName, jobName, filters))
                    continue;

                // Extract inline issues (error messages) from the task record
                var issues = new List<string>();
                if (rec.TryGetProperty("issues", out var issuesProp))
                {
                    foreach (var issue in issuesProp.EnumerateArray())
                    {
                        var issueType = issue.TryGetProperty("type", out var it) ? it.GetString() : null;
                        // Collect errors always; for succeededWithIssues tasks also collect warnings
                        if (issueType == "error" || (issueType == "warning" && result == "succeededWithIssues"))
                        {
                            var msg = issue.TryGetProperty("message", out var im) ? im.GetString() : null;
                            if (msg is not null)
                                issues.Add(msg.Length > 500 ? msg[..500] : msg);
                        }
                    }
                }

                failedTasks.Add(new FailedTask
                {
                    TaskName = taskName,
                    JobName = jobName,
                    StageName = stageName,
                    LogId = logId,
                    Result = result ?? "failed",
                    ErrorMessages = issues,
                });
            }

            return new PipelineFailureInfo
            {
                BuildId = buildId,
                FailedTasks = failedTasks,
            };
        }
        catch (Exception ex)
        {
            return new PipelineFailureInfo { BuildId = buildId, Error = $"Failed to parse timeline: {ex.Message}" };
        }
    }

    /// <summary>
    /// Fetches the tail of a build task log.
    /// </summary>
    public static async Task<string> GetTaskLogAsync(
        string orgUrl, string project, int buildId, int logId, int tailLines = 50)
    {
        var output = await RunAzAsync(
            $"devops invoke --area build --resource logs " +
            $"--route-parameters project={project} buildId={buildId} logId={logId} " +
            $"--org {orgUrl} -o json");

        if (output is null)
            return "Failed to fetch log.";

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("value", out var lines))
            {
                var allLines = lines.EnumerateArray().Select(l => l.GetString() ?? "").ToList();
                var tail = allLines.Skip(Math.Max(0, allLines.Count - tailLines)).ToList();
                return string.Join("\n", tail);
            }
            return "Log format unexpected — no 'value' array.";
        }
        catch (Exception ex)
        {
            return $"Failed to parse log: {ex.Message}";
        }
    }

    private static List<(string StagePattern, List<string>? JobPatterns)> ParseStageFilters(string? stageFilters)
    {
        if (string.IsNullOrWhiteSpace(stageFilters))
            return [];

        var result = new List<(string, List<string>?)>();
        foreach (var segment in stageFilters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Contains("(all jobs)"))
            {
                var pattern = segment.Replace("(all jobs)", "").Trim();
                result.Add((pattern, null));
            }
            else if (segment.Contains('→'))
            {
                var parts = segment.Split('→', 2, StringSplitOptions.TrimEntries);
                var jobs = parts[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                result.Add((parts[0], jobs));
            }
            else
            {
                result.Add((segment.Trim(), null));
            }
        }
        return result;
    }

    private static bool MatchesFilters(
        string? stageName, string? jobName,
        List<(string StagePattern, List<string>? JobPatterns)> filters)
    {
        if (stageName is null) return true; // can't filter without a stage name

        foreach (var (stagePattern, jobPatterns) in filters)
        {
            if (!GlobMatch(stageName, stagePattern))
                continue;

            // Stage matches — check jobs
            if (jobPatterns is null)
                return true; // all jobs in this stage

            if (jobName is null)
                return true; // can't filter jobs without a name

            if (jobPatterns.Any(jp => GlobMatch(jobName, jp)))
                return true;
        }

        return false;
    }

    /// <summary>Simple glob match supporting * wildcard.</summary>
    private static bool GlobMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
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
}

/// <summary>Info about pipeline failures from the build timeline.</summary>
public sealed class PipelineFailureInfo
{
    public int BuildId { get; init; }
    public string? Error { get; init; }
    public List<FailedTask> FailedTasks { get; init; } = [];
}

/// <summary>A single failed or canceled task in the build timeline.</summary>
public sealed class FailedTask
{
    public string TaskName { get; init; } = "";
    public string? JobName { get; init; }
    public string? StageName { get; init; }
    public int? LogId { get; init; }
    /// <summary>The timeline result: "failed" or "canceled" (timeout).</summary>
    public string Result { get; init; } = "failed";
    public List<string> ErrorMessages { get; init; } = [];
}
