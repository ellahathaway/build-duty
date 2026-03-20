using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.AI;

/// <summary>
/// Persists AI run results as individual JSON files.
/// </summary>
public sealed class AiRunStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directory;

    public AiRunStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(AiRunResult result, CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, $"{result.RunId}.json");
        result.ArtifactPath = path;
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, result, s_options, ct);
    }

    public async Task<AiRunResult?> LoadAsync(string runId, CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, $"{runId}.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AiRunResult>(stream, s_options, ct);
    }

    /// <summary>
    /// Find the most recent run for a given work item, if any.
    /// </summary>
    public async Task<AiRunResult?> FindLatestForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return null;

        AiRunResult? latest = null;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var run = await JsonSerializer.DeserializeAsync<AiRunResult>(stream, s_options, ct);
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
}
