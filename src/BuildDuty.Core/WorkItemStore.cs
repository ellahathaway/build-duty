using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

/// <summary>
/// Persists work items as individual JSON files under a configurable directory.
/// </summary>
public sealed class WorkItemStore
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directory;

    public WorkItemStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string BasePath => _directory;

    public async Task SaveAsync(WorkItem item, CancellationToken ct = default)
    {
        var path = GetPath(item.Id);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, item, s_options, ct);
    }

    public async Task<WorkItem?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = GetPath(id);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkItem>(stream, s_options, ct);
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(
        WorkItemState? stateFilter = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return [];

        var files = Directory.GetFiles(_directory, "*.json");
        var items = new List<WorkItem>();

        foreach (var file in files.OrderBy(f => f))
        {
            await using var stream = File.OpenRead(file);
            var item = await JsonSerializer.DeserializeAsync<WorkItem>(stream, s_options, ct);
            if (item is null) continue;
            if (stateFilter.HasValue && item.State != stateFilter.Value) continue;
            items.Add(item);
            if (limit.HasValue && items.Count >= limit.Value) break;
        }

        return items;
    }

    public bool Exists(string id) => File.Exists(GetPath(id));

    private string GetPath(string id) => Path.Combine(_directory, $"{id}.json");
}
