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

    private readonly IBuildDutyConfigProvider? _configProvider;
    private readonly string? _directoryOverride;

    public WorkItemStore(IBuildDutyConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    internal WorkItemStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directoryOverride = directory;
    }

    public async Task SaveAsync(WorkItem item, CancellationToken ct = default)
    {
        var directory = GetDirectory();
        var path = Path.Combine(directory, $"{item.Id}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, item, s_options, ct);
    }

    public async Task<WorkItem?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = Path.Combine(GetDirectory(), $"{id}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkItem>(stream, s_options, ct);
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(
        bool? resolved = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var directory = GetDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = Directory.GetFiles(directory, "*.json");
        var items = new List<WorkItem>();

        foreach (var file in files.OrderBy(f => f))
        {
            await using var stream = File.OpenRead(file);
            var item = await JsonSerializer.DeserializeAsync<WorkItem>(stream, s_options, ct);
            if (item is null)
            {
                continue;
            }

            if (resolved.HasValue && item.IsResolved != resolved.Value)
            {
                continue;
            }

            items.Add(item);
            if (limit.HasValue && items.Count >= limit.Value)
            {
                break;
            }
        }

        return items;
    }

    public bool Exists(string id) => File.Exists(Path.Combine(GetDirectory(), $"{id}.json"));

    private string GetDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_directoryOverride))
        {
            Directory.CreateDirectory(_directoryOverride);
            return _directoryOverride;
        }

        if (_configProvider is null)
        {
            throw new InvalidOperationException("Config provider was not initialized.");
        }

        var config = _configProvider.Get();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".build-duty",
            config.Name,
            "work-items");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
