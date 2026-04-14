using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public interface IStorageProvider
{
    Task SaveWorkItemAsync(WorkItem workItem);
    Task<WorkItem> GetWorkItemAsync(string workItemId);
    Task<ICollection<WorkItem>> GetWorkItemsAsync();
    Task SaveSignalAsync(Signal signal);
    Task DeleteSignalAsync(string signalId);
    Task<Signal> GetSignalAsync(string signalId);
    Task SaveTriageRunAsync(TriageRun triageRun);
    Task<TriageRun> GetTriageRunAsync(string triageId);
    Task<ICollection<TriageRun>> GetTriageRunsAsync();
    StreamWriter CreateAgentLogWriter(string triageId, string logName);
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
            new SignalJsonConverter(),
        }
    };

    private readonly Lazy<string> _rootDirectory;

    public StorageProvider(IBuildDutyConfigProvider configProvider)
    {
        _rootDirectory = new Lazy<string>(() =>
        {
            var configName = configProvider.Get().Name;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".build-duty", configName);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "workitems"));
            Directory.CreateDirectory(Path.Combine(root, "signals"));
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

    public async Task SaveSignalAsync(Signal signal)
        => await SaveToJsonFileAsync(GetSignalFilePath(signal.Id), signal);

    public async Task DeleteSignalAsync(string signalId)
    {
        var filePath = GetSignalFilePath(signalId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    public async Task<Signal> GetSignalAsync(string signalId)
        => await LoadFromJsonFileAsync<Signal>(GetSignalFilePath(signalId));

    public async Task SaveTriageRunAsync(TriageRun triageRun)
        => await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);

    public async Task<TriageRun> GetTriageRunAsync(string triageRunId)
        => await LoadFromJsonFileAsync<TriageRun>(GetTriageRunFilePath(triageRunId));

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

    public StreamWriter CreateAgentLogWriter(string triageId, string logName)
    {
        var dir = Path.Combine(GetTriageRunsDirectory(), triageId, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{logName}.jsonl");
        return new StreamWriter(path, append: true) { AutoFlush = true };
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
    private string GetSignalDirectory() => Path.Combine(_rootDirectory.Value, "signals");
    private string GetTriageRunsDirectory() => Path.Combine(_rootDirectory.Value, "triage");

    private string GetWorkItemFilePath(string workItemId) => Path.Combine(GetWorkItemsDirectory(), $"{workItemId}.json");
    private string GetSignalFilePath(string signalId) => Path.Combine(GetSignalDirectory(), $"{signalId}.json");
    private string GetTriageRunFilePath(string triageRunId) => Path.Combine(GetTriageRunsDirectory(), $"{triageRunId}.json");
}

internal sealed class SignalJsonConverter : JsonConverter<Signal>
{
    public override Signal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            throw new JsonException("Missing required property 'type'.");
        }

        SignalType type = Enum.Parse<SignalType>(typeProp.GetString()!, ignoreCase: true);

        return type switch
        {
            SignalType.GitHubIssue =>
                root.Deserialize<GitHubIssueSignal>(options) ?? throw new JsonException("Failed to deserialize GitHubIssueSignal."),

            SignalType.GitHubPullRequest =>
                root.Deserialize<GitHubPullRequestSignal>(options) ?? throw new JsonException("Failed to deserialize GitHubPullRequestSignal."),

            SignalType.AzureDevOpsPipeline =>
                root.Deserialize<AzureDevOpsPipelineSignal>(options) ?? throw new JsonException("Failed to deserialize AzureDevOpsPipelineSignal."),

            _ => throw new JsonException($"Unsupported signal type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Signal value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}
