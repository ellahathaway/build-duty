using System.Text.Json;
using System.Text.Json.Serialization;
using BuildDuty.Core;

namespace BuildDuty.AI;

/// <summary>
/// Persists AI run results as individual JSON files.
/// </summary>
public sealed class TriageStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IBuildDutyConfigProvider _configProvider;

    public TriageStore(IBuildDutyConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public async Task SaveAsync(TriageResult result, CancellationToken ct = default)
    {
        var path = Path.Combine(GetDirectory(), $"{result.RunId}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, s_options, ct);
    }

    public async Task<TriageResult?> LoadAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetDirectory(), $"{runId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TriageResult>(stream, s_options, ct);
    }

    /// <summary>
    /// Find the most recent run for a given work item, if any.
    /// </summary>
    public async Task<TriageResult?> FindLatestForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        var directory = GetDirectory();
        if (!Directory.Exists(directory))
        {
            return null;
        }

        TriageResult? latest = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var run = await JsonSerializer.DeserializeAsync<TriageResult>(stream, s_options, ct);
                if (run?.WorkItemId == workItemId &&
                    (latest is null || run.FinishedUtc > latest.FinishedUtc))
                {
                    latest = run;
                }
            }
            catch { /* skip corrupt files */ }
        }

        return latest;
    }

    private string GetDirectory()
    {
        var config = _configProvider.Get();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".build-duty",
            config.Name,
            "triage-runs");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
