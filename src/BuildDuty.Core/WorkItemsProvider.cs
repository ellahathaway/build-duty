using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public interface IWorkItemsProvider
{
    Task<IEnumerable<WorkItem>> GetWorkItemsAsync(Enum? signalType = null, CancellationToken ct = default);
}

/// <summary>
/// Persists work items as individual JSON files under a configurable directory.
/// </summary>
public sealed class WorkItemsProvider : IWorkItemsProvider
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lazy<string> _directory;

    public WorkItemsProvider(IBuildDutyConfigProvider configProvider)
    {
        _directory = new Lazy<string>(() =>
        {
            var configName = configProvider.GetConfig().Name;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".build-duty", configName, "workitems");
            Directory.CreateDirectory(dir);
            return dir;
        });
    }

    public async Task<IEnumerable<WorkItem>> GetWorkItemsAsync(
        Enum? signalType = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory.Value))
        {
            return Enumerable.Empty<WorkItem>();
        }

        var files = Directory.GetFiles(_directory.Value, "*.json");

        var fileTasks = files.OrderBy(f => f).Select(async file =>
        {
            await using var stream = File.OpenRead(file);
            var item = await JsonSerializer.DeserializeAsync<WorkItem>(stream, s_options, ct)
                ?? throw new InvalidDataException($"Failed to deserialize work item from file '{file}'.");

            if (signalType is not null && !item.Signals.Any(signal => signal.Type.Equals(signalType)))
            {
                return null;
            }
            return item;
        });

        return (await Task.WhenAll(fileTasks)).Where(item => item is not null)!;
    }
}
