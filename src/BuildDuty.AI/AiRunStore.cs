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
}
