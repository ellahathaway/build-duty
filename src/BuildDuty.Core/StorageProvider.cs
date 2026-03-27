using System.Text.Json;
using System.Text.Json.Serialization;
using Octokit;

namespace BuildDuty.Core;

public interface IStorageProvider
{
    Task SaveWorkItemAsync(WorkItem workItem);
    Task<WorkItem> GetWorkItemAsync(string workItemId);
    Task<ICollection<WorkItem>> GetWorkItemsAsync();
    Task SaveSignalAsync(ISignal signal);
    Task<ISignal> GetSignalAsync(string signalId);
    Task SaveTriageRunAsync(TriageRun triageRun);
    Task<TriageRun> GetTriageRunAsync(string triageId);
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
            new ItemStateJsonConverter(),
            new JsonStringEnumConverter(),
            new SignalConverter(),
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

    public async Task SaveSignalAsync(ISignal signal)
        => await SaveToJsonFileAsync(GetSignalFilePath(signal.Id), signal);

    public async Task<ISignal> GetSignalAsync(string signalId)
        => await LoadFromJsonFileAsync<ISignal>(GetSignalFilePath(signalId));

    public async Task SaveTriageRunAsync(TriageRun triageRun)
        => await SaveToJsonFileAsync(GetTriageRunFilePath(triageRun.Id), triageRun);

    public async Task<TriageRun> GetTriageRunAsync(string triageRunId)
        => await LoadFromJsonFileAsync<TriageRun>(GetTriageRunFilePath(triageRunId));

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
    private string GetSignalsDirectory() => Path.Combine(_rootDirectory.Value, "signals");
    private string GetTriageRunsDirectory() => Path.Combine(_rootDirectory.Value, "triage");

    private string GetWorkItemFilePath(string workItemId) => Path.Combine(GetWorkItemsDirectory(), $"{workItemId}.json");
    private string GetSignalFilePath(string signalId) => Path.Combine(GetSignalsDirectory(), $"{signalId}.json");
    private string GetTriageRunFilePath(string triageRunId) => Path.Combine(GetTriageRunsDirectory(), $"{triageRunId}.json");
}

internal sealed class SignalConverter : JsonConverter<ISignal>
{
    public override ISignal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

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

    public override void Write(Utf8JsonWriter writer, ISignal value, JsonSerializerOptions options)
    {
        // Easiest: serialize the runtime type; it already has the "Type" property,
        // which will be written as "type" by default naming rules.
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}

public sealed class ItemStateJsonConverter : JsonConverter<ItemState>
{
    public override ItemState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return Parse(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);

            if (doc.RootElement.TryGetProperty("stringValue", out var sv) &&
                sv.ValueKind == JsonValueKind.String)
            {
                return Parse(sv.GetString());
            }

            if (doc.RootElement.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                return Parse(v.GetString());
            }

            throw new JsonException("Invalid ItemState object; expected stringValue or value.");
        }

        throw new JsonException($"Unexpected token {reader.TokenType} for ItemState.");
    }

    public override void Write(Utf8JsonWriter writer, ItemState value, JsonSerializerOptions options)
    {
        // prefer the compact/stable representation
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }

    private static ItemState Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new JsonException("ItemState was empty.");
        }

        return s.Trim().ToLowerInvariant() switch
        {
            "open" => ItemState.Open,
            "closed" => ItemState.Closed,
            _ => throw new JsonException($"Value '{s}' is not a valid ItemState.")
        };
    }
}
