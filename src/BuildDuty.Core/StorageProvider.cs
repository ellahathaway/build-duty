using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public interface IStorageProvider
{
    Task SaveWorkItemAsync(WorkItem workItem);
    Task<WorkItem> GetWorkItemAsync(string workItemId);
    Task<ICollection<WorkItem>> GetWorkItemsAsync();
    Task SaveSignalAsync(Signal signal);
    Task<Signal> GetSignalAsync(string signalId);
    Task SaveTriageRunAsync(TriageRun triageRun);
    Task<TriageRun> GetTriageRunAsync(string triageId);
    Task<string> SaveSignalLogAsync(string signalId, string logId, Stream logsStream);
    Task<string> GetSignalLogPathAsync(string signalId, string logId);
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

    public async Task<Signal> GetSignalAsync(string signalId)
        => await LoadFromJsonFileAsync<Signal>(GetSignalFilePath(signalId));

    public async Task SaveTriageRunAsync(TriageRun triageRun)
        => await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);

    public async Task<TriageRun> GetTriageRunAsync(string triageRunId)
        => await LoadFromJsonFileAsync<TriageRun>(GetTriageRunFilePath(triageRunId));

    public async Task<string> SaveSignalLogAsync(string signalId, string logId, Stream logsStream)
    {
        var logFile = GetSignalLogFilePath(signalId, logId);
        if (!Directory.Exists(Path.GetDirectoryName(logFile)))
        {
            throw new DirectoryNotFoundException($"Signal directory not found for signal ID '{signalId}'.");
        }
        using var fileStream = File.Create(logFile);
        await logsStream.CopyToAsync(fileStream);
        return logFile;
    }

    public async Task<string> GetSignalLogPathAsync(string signalId, string logId)
    {
        var logFile = GetSignalLogFilePath(signalId, logId);
        return File.Exists(logFile)
            ? logFile
            : throw new FileNotFoundException($"Log file '{logId}' not found for signal ID '{signalId}'.");
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
    private string GetSignalDirectory(string signalId) => Path.Combine(_rootDirectory.Value, "signals", signalId);
    private string GetTriageRunsDirectory() => Path.Combine(_rootDirectory.Value, "triage");

    private string GetWorkItemFilePath(string workItemId) => Path.Combine(GetWorkItemsDirectory(), $"{workItemId}.json");
    private string GetSignalFilePath(string signalId) => Path.Combine(GetSignalDirectory(signalId), $"{signalId}.json");
    private string GetTriageRunFilePath(string triageRunId) => Path.Combine(GetTriageRunsDirectory(), $"{triageRunId}.json");
    private string GetSignalLogFilePath(string signalId, string logId) => Path.Combine(GetSignalDirectory(signalId), $"{logId}.log");
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
