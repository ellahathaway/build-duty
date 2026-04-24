using System.Text.Json;
using System.Text.Json.Serialization;
using BuildDuty.Signals;
using BuildDuty.Signals.Configuration;

namespace BuildDuty.Cli.Infrastructure;

public interface IStorageProvider
{
    Task SaveWorkItemAsync(WorkItem workItem);
    Task<WorkItem> GetWorkItemAsync(string workItemId);
    Task<ICollection<WorkItem>> GetWorkItemsAsync();
    Task UpdateTriageRunStatusAsync(string triageId, TriageRunStatus status);
    Task<TriageRun> GetTriageRunAsync(string triageId);
    Task SaveSignalsToTriageRun(string triageId, List<Signal> signals);
    Task<ICollection<TriageRun>> GetTriageRunsAsync();
}

public sealed class StorageProvider : IStorageProvider
{
    private static readonly JsonSerializerOptions s_options = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(),
        }
    };

    private readonly Lazy<string> _rootDirectory;

    public StorageProvider(string name)
    {
        _rootDirectory = new Lazy<string>(() =>
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".build-duty", name);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "workitems"));
            Directory.CreateDirectory(Path.Combine(root, "triage"));
            return root;
        });
    }

    public async Task SaveWorkItemAsync(WorkItem workItem)
        => await SaveToJsonFileAsync(GetWorkItemFilePath(workItem.Id), workItem);

    public async Task<WorkItem> GetWorkItemAsync(string workItemId)
        => await LoadFromJsonFileAsync<WorkItem>(GetWorkItemFilePath(workItemId));

    public async Task<ICollection<WorkItem>> GetWorkItemsAsync()
    {
        var directory = GetWorkItemsDirectory();
        if (!Directory.Exists(directory))
        {
            return Array.Empty<WorkItem>();
        }

        var loadTasks = Directory.GetFiles(directory, "*.json")
            .Select(file => LoadFromJsonFileAsync<WorkItem>(file));
        return await Task.WhenAll(loadTasks);
    }

    public async Task UpdateTriageRunStatusAsync(string triageId, TriageRunStatus status)
    {
        var triageRun = await GetTriageRunAsync(triageId);
        triageRun.Status = status;
        await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);
    }

    public async Task SaveSignalsToTriageRun(string triageId, List<Signal> signals)
    {
        var triageRun = await GetTriageRunAsync(triageId);
        string signalsXmlPath = Path.Combine(GetTriageRunsDirectory(), $"{triageRun.Id}_signals.xml");
        if (File.Exists(signalsXmlPath))
        {
            File.Delete(signalsXmlPath);
        }
        SignalXmlSerializer.SerializeToFile(signals, signalsXmlPath);
        triageRun.SignalsXmlPath = signalsXmlPath;
        await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);
    }

    public async Task<TriageRun> GetTriageRunAsync(string? triageRunId = null)
    {
        if (triageRunId is null)
        {
            var triageRun = new TriageRun();
            await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);
            return triageRun;
        }
        return await LoadFromJsonFileAsync<TriageRun>(GetTriageRunFilePath(triageRunId));
    }

    public async Task<ICollection<TriageRun>> GetTriageRunsAsync()
    {
        var directory = GetTriageRunsDirectory();
        if (!Directory.Exists(directory))
        {
            return Array.Empty<TriageRun>();
        }

        var loadTasks = Directory.GetFiles(directory, "*.json")
            .Select(file => LoadFromJsonFileAsync<TriageRun>(file));
        return await Task.WhenAll(loadTasks);
    }

    private static async Task SaveToJsonFileAsync(string filePath, object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, data, s_options);
    }

    private static async Task<T> LoadFromJsonFileAsync<T>(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File '{filePath}' not found.");
        }

        await using var stream = File.OpenRead(filePath);
        var data = await JsonSerializer.DeserializeAsync<T>(stream, s_options);
        if (data is null)
        {
            throw new InvalidDataException($"Failed to deserialize data from file '{filePath}'.");
        }

        return data;
    }

    private string GetWorkItemsDirectory() => Path.Combine(_rootDirectory.Value, "workitems");
    private string GetTriageRunsDirectory() => Path.Combine(_rootDirectory.Value, "triage");
    private string GetWorkItemFilePath(string workItemId) => Path.Combine(GetWorkItemsDirectory(), $"{workItemId}.json");
    private string GetTriageRunFilePath(string triageRunId) => Path.Combine(GetTriageRunsDirectory(), $"{triageRunId}.json");
}
